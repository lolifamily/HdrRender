using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ClientPlugin.Rendering;
using HarmonyLib;
using SharpDX.Mathematics.Interop;
using VRage.Render11.RenderContext;
using VRage.Render11.Resources;
using VRage.Render11.Sprites;

namespace ClientPlugin.Patches;

// UI brightness, applied by the blend unit while the UI is drawn.
//
// MySpritesManager.Render draws every batch with one blend state, BlendAlphaPremult
// (One / InvSrcAlpha), and Sprites.hlsl outputs premultiplied color: the vertex shader
// multiplies the vertex color by alpha, the pixel shader the texture sample too under
// PREMULTIPLY_ALPHA. Videos are sprites as well (MyVideoPlayer.Draw). On the scRGB
// render target UI white has to land at UI brightness, not at 1.0 = 80 nits, so there
// the same blend runs with SrcBlend = BlendFactor and factor (k, k, k, 1):
//     dst = k * src.rgb + (1 - src.a) * dst
// Premultiplied "over" is associative and linear in rgb, so this equals a premultiplied
// UI layer composited at k - without the layer and without the composite pass. D3D11
// does not clamp the blend factor on float16 targets (Output-Merger stage), so k > 1 holds.
//
// Keyed on the target: the backbuffer and the screenshot targets (swapchain format) are
// scRGB. LCD screens blend with BlendAlphaPremultNoAlphaChannel, other offscreen sprite
// targets are 8-bit; both keep the engine's blend.
//
// A transpiler, because the factor is set in the same call as the state and Render passes
// none: every rc.SetBlendState in Render goes through SetBlendState below, which also gets
// Render's target.
[HarmonyPatch(typeof(MySpritesManager), nameof(MySpritesManager.Render))]
internal static class UiBlendPatch
{
    private static readonly MethodInfo EngineSetBlendState = AccessTools.Method(typeof(MyRenderContext),
        nameof(MyRenderContext.SetBlendState), [typeof(IBlendState), typeof(RawColor4?)]);

    private static readonly MethodInfo UiSetBlendState = AccessTools.Method(typeof(UiBlendPatch), nameof(SetBlendState));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var codes = new List<CodeInstruction>(instructions);
        var rtvIndex = Array.FindIndex(original.GetParameters(), p => p.ParameterType == typeof(IRtvBindable));
        var replaced = 0;
        for (var i = 0; rtvIndex >= 0 && i < codes.Count; i++)
        {
            if (!codes[i].Calls(EngineSetBlendState))
                continue;

            // Stack: rc, blend state, factor. Push the target too and call ours instead; labels
            // and blocks move to the push, so a jump to the call still passes all four.
            var push = CodeInstruction.LoadArgument(rtvIndex + 1); // arg 0 is this
            push.labels.AddRange(codes[i].labels);
            push.blocks.AddRange(codes[i].blocks);
            codes[i] = new CodeInstruction(OpCodes.Call, UiSetBlendState);
            codes.Insert(i++, push);
            replaced++;
        }

        if (replaced == 0)
            VRage.Utils.MyLog.Default.WriteLine(
                "HDR: MySpritesManager.Render: no SetBlendState call to hook -> UI stays at 80 nits (engine layout changed?)");
        return codes;
    }

    private static void SetBlendState(MyRenderContext rc, IBlendState blendState, RawColor4? blendFactor, IRtvBindable rtv)
    {
        if (!ReferenceEquals(blendState, MyBlendStateManager.BlendAlphaPremult) || !IsScRgb(rtv))
        {
            rc.SetBlendState(blendState, blendFactor);
            return;
        }

        var graphicsWhite = Config.Current.UiBrightness / 80f;
        rc.SetBlendState(HdrResources.UiBlendState, new RawColor4(graphicsWhite, graphicsWhite, graphicsWhite, 1f));
    }

    internal static bool IsScRgb(IRtvBindable rtv) =>
        rtv?.Rtv is { } view && view.Description.Format == HdrResources.BackbufferFormat;
}
