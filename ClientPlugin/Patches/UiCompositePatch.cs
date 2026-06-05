using System;
using ClientPlugin.Rendering;
using HarmonyLib;
using SharpDX.Direct3D;
using SharpDX.DXGI;
using SharpDX.Mathematics.Interop;
using VRage.Render11.Common;
using VRage.Render11.Resources;
using VRageMath;
using VRageRender;

namespace ClientPlugin.Patches;

// Per-frame UI offscreen slot, owned by the render thread.
//
// Lifetime: RenderMainSprites Prefix borrows, ConsumeMainSprites Postfix releases.
// The two patches sit on different methods so Harmony's __state cannot carry the
// borrow across them — we keep it in a static slot here.
//
// Plain static is correct: SE's D3D11 pipeline is single-threaded by construction
// (ImmediateContext, RwTexturesPool, RC are all non-thread-safe), so this code only
// ever runs on the render thread. There is no cross-thread contention to defend
// against — any off-thread access would have already crashed inside RC before
// reaching this slot, so [ThreadStatic] would be cargo-cult here.
//
// Contract: after Consume releases the texture it MUST null the slot, so the next
// Render Prefix does not double-release a dead handle.
internal static class UiOffscreenSlot
{
    public static IBorrowedRtvTexture Current { get; private set; }
    public static IRtvBindable OverrideTarget { get; private set; }

    // Replace the current slot with a freshly borrowed RTV. Any leftover from a previous
    // frame (i.e. Consume never ran due to an exception) is released first so the pool
    // counters stay sane. OverrideTarget is reset as part of the invariant: a fresh slot
    // composites to Backbuffer by default; callers that need to hijack the composite
    // target MUST call SetOverrideTarget after Replace. Without this reset an exception
    // on the screenshot path would leave a stale OverrideTarget pointing at a returned-
    // to-pool RTV, and the next normal frame would composite UI into the wrong RTV
    // (re-borrowed by some other pass or, after 16 frames, a disposed RenderTargetView).
    public static void Replace(IBorrowedRtvTexture freshlyBorrowed)
    {
        Current?.Release();
        Current = freshlyBorrowed;
        OverrideTarget = null;
    }

    public static void SetOverrideTarget(IRtvBindable target) => OverrideTarget = target;

    // Consume side: release the texture and clear the slot. After this point Current
    // returns null and the next Render Prefix sees a clean state.
    public static void ReleaseAndClear()
    {
        Current?.Release();
        Current = null;
        OverrideTarget = null;
    }
}

[HarmonyPatch(typeof(MyRender11), nameof(MyRender11.RenderMainSprites), [])]
internal static class RenderMainSpritesPatch
{
    private static bool Prefix()
    {
        SwapChainReplacer.ReplaceSwapChain();

        var res = MyRender11.ViewportResolution;
        var uiOff = MyManagers.RwTexturesPool.BorrowRtv(
            "HdrOutput.UiOffscreen",
            res.X, res.Y,
            Format.R8G8B8A8_UNorm_SRgb);
        UiOffscreenSlot.Replace(uiOff);

        MyImmediateRC.RC.ClearRtv(uiOff, new RawColor4(0, 0, 0, 0));

        var viewport = new MyViewport(res.X, res.Y);
        var scaledViewport = MyRender11.ScaleMainViewport(viewport);

        MyRender11.RenderMainSprites(
            uiOff,
            scaledViewport,
            viewport,
            new Vector2(res.X, res.Y));

        return false;
    }

    // Plugin self-disable on crash. Any exception that bubbles through patched
    // RenderMainSprites (our Prefix, the original method, downstream Postfixes,
    // or other plugins' patches on this method) sets DisabledAfterCrash and
    // flushes to disk. The next launch reads the flag in Plugin.Init and skips
    // patching entirely. User clears the flag in plugin config UI to retry.
    //
    // Save() can throw on IO failure - log and continue so the original render
    // crash exception keeps its place in SE's minidump.
    private static Exception Finalizer(Exception __exception)
    {
        if (__exception == null) return null;

        Config.Current.DisabledAfterCrash = true;
        try { Settings.ConfigStorage.Save(Config.Current); }
        catch (Exception saveEx)
        {
            VRage.Utils.MyLog.Default.WriteLine($"HDR: Failed to persist DisabledAfterCrash flag: {saveEx.Message}");
        }
        return __exception;
    }
}

[HarmonyPatch(typeof(MyRender11), nameof(MyRender11.RenderMainSprites),
    typeof(IRtvBindable), typeof(MyViewport), typeof(MyViewport), typeof(Vector2), typeof(MyViewport?))]
internal static class RenderMainSprites4Patch
{
    private static bool Prefix(IRtvBindable rtv, MyViewport viewportBound, MyViewport viewportFull, Vector2 size, MyViewport? targetRegion)
    {
        // Sentinel: our own re-entry, let the original method run.
        if (ReferenceEquals(rtv, UiOffscreenSlot.Current))
            return true;

        // External caller (e.g. screenshot path): hijack the UI render into our offscreen
        // and remember the real target so ConsumeMainSpritesPatch composites onto it.
        // Order matters: Replace() resets OverrideTarget to null as part of its invariant,
        // so SetOverrideTarget MUST run after Replace - otherwise the hijack target is
        // wiped before it takes effect.
        var uiOff = MyManagers.RwTexturesPool.BorrowRtv(
            "HdrOutput.ScreenshotUiOffscreen",
            rtv.Size.X, rtv.Size.Y,
            Format.R8G8B8A8_UNorm_SRgb);
        MyImmediateRC.RC.ClearRtv(uiOff, new RawColor4(0, 0, 0, 0));
        UiOffscreenSlot.Replace(uiOff);
        UiOffscreenSlot.SetOverrideTarget(rtv);

        // Re-enters this Prefix; the sentinel above lets the call through to the original.
        MyRender11.RenderMainSprites(uiOff, viewportBound, viewportFull, size, targetRegion);

        return false;
    }
}

[HarmonyPatch(typeof(MyRender11), nameof(MyRender11.ConsumeMainSprites))]
internal static class ConsumeMainSpritesPatch
{
    private static void Postfix()
    {
        var uiLayer = UiOffscreenSlot.Current;
        if (uiLayer == null)
            return;

        var rc = MyImmediateRC.RC;
        var device = MyRender11.DeviceInstance;

        var cfg = Config.Current;
        var ctx = device.ImmediateContext;
        var mapped = ctx.MapSubresource(HdrResources.UiConstantBuffer.Resource, 0,
            SharpDX.Direct3D11.MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None);
        var pw = cfg.PaperWhite / 80f;
        SharpDX.Utilities.Write(mapped.DataPointer, ref pw);
        ctx.UnmapSubresource(HdrResources.UiConstantBuffer.Resource, 0);

        rc.SetBlendState(MyBlendStateManager.BlendAlphaPremult);
        rc.SetInputLayout(null);
        rc.SetPrimitiveTopology(PrimitiveTopology.TriangleList);
        rc.SetDepthStencilState(MyDepthStencilStateManager.IgnoreDepthStencil);

        rc.VertexShader.Set(HdrResources.UiCompositeVertexShader);
        rc.PixelShader.Set(HdrResources.UiCompositePixelShader);
        rc.PixelShader.SetConstantBuffer(0, HdrResources.UiConstantBuffer);
        rc.PixelShader.SetSrv(0, uiLayer);
        rc.PixelShader.SetSampler(0, MySamplerStateManager.Point);

        var compositeTarget = UiOffscreenSlot.OverrideTarget ?? MyRender11.Backbuffer;
        rc.SetRtv(compositeTarget);
        var bbSize = compositeTarget.Size;
        rc.SetViewport(0, 0, bbSize.X, bbSize.Y);
        rc.Draw(3, 0);
        rc.SetRtvNull();

        rc.PixelShader.SetSrv(0, null);

        // Release the borrow AND null the slot in one step. Without this null, the next
        // RenderMainSprites Prefix would call Release() again on this same dead handle.
        UiOffscreenSlot.ReleaseAndClear();
    }
}
