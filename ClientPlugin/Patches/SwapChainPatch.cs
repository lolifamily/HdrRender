using System;
using ClientPlugin.Rendering;
using HarmonyLib;
using SharpDX.DXGI;
using SharpDX.Direct3D11;
using VRage;
using VRage.Platform.Windows.Render;
using VRage.Render11.Resources;
using VRageRender;

namespace ClientPlugin.Patches;

internal static class SwapChainReplacer
{
    // Driver-reported AllowTearing support. Probed once at plugin Init;
    // false on Win10 < 1709 (Factory5 unavailable) or drivers that opt out.
    // Read by Present-flag patch to decide whether to force tearing path.
    public static bool AllowTearingSupported { get; private set; }

    // Device-level frame latency. Per-swapchain SetMaximumFrameLatency would
    // require the swapchain to be created with FrameLatencyWaitableObject and
    // for the renderer to actively wait on the handle, which SE does not.
    // Device1.MaximumFrameLatency works without either: it caps the GPU queue
    // depth for every swapchain bound to this device (we only have one).
    public static void ApplyMaximumFrameLatency()
    {
        try
        {
            using var dxgiDevice1 = MyRender11.DeviceInstance.QueryInterface<SharpDX.DXGI.Device1>();
            dxgiDevice1.MaximumFrameLatency = Config.Current.LowLatencyMode ? 1 : 3;
        }
        catch (Exception e)
        {
            VRage.Utils.MyLog.Default.WriteLine($"HDR: SetMaximumFrameLatency failed: {e.Message}");
        }
    }

    // Called once from Plugin.Init. Probes DXGI for AllowTearing capability so
    // the Config switch can be honored (or silently ignored on unsupported systems).
    public static void ProbeFeatureSupport()
    {
        try
        {
            using var dxgiDevice = MyRender11.DeviceInstance.QueryInterface<SharpDX.DXGI.Device>();
            using var adapter = dxgiDevice.Adapter;
            using var factory = adapter.GetParent<Factory1>();
            using var factory5 = factory.QueryInterface<Factory5>();
            AllowTearingSupported = factory5.PresentAllowTearing;
            VRage.Utils.MyLog.Default.WriteLine($"HDR: AllowTearing supported = {AllowTearingSupported}");
        }
        catch (Exception e)
        {
            AllowTearingSupported = false;
            VRage.Utils.MyLog.Default.WriteLine($"HDR: AllowTearing probe failed (assuming unsupported): {e.Message}");
        }
    }

    public static void ReplaceSwapChain()
    {
        if (HdrResources.Initialized)
            return;

        var currentSwapchain = MyRender11.m_swapchain;

        var hwnd = currentSwapchain.Description.OutputHandle;
        var device = MyRender11.DeviceInstance;

        // DXGI requires SetFullscreenState(false) before the final release of a
        // fullscreen swapchain, and the hwnd must not be owned by a fullscreen
        // swapchain when CreateSwapChainForHwnd is called for a new one on the
        // same hwnd. If SE started directly into Fullscreen mode (or restored
        // it from saved settings), the stock swapchain is already fullscreen
        // by the time we get here.
        try { currentSwapchain.SetFullscreenState(false, null); }
        catch (Exception e)
        {
            VRage.Utils.MyLog.Default.WriteLine($"HDR: SetFullscreenState(false) on old swapchain failed: {e.Message}");
        }

        MyRender11.Backbuffer?.Release();

        currentSwapchain.Dispose();

        using var dxgiDevice = device.QueryInterface<SharpDX.DXGI.Device>();
        using var adapter = dxgiDevice.Adapter;
        using var factory1 = adapter.GetParent<Factory1>();
        using var factory2 = factory1.QueryInterface<Factory2>();

        var currentSettings = MyPlatformRender.m_settings;
        var flags = SwapChainFlags.AllowModeSwitch;
        if (Config.Current.AllowTearing && AllowTearingSupported)
            flags |= SwapChainFlags.AllowTearing;

        var desc = new SwapChainDescription1
        {
            Width = currentSettings.BackBufferWidth,
            Height = currentSettings.BackBufferHeight,
            Format = HdrResources.BackbufferFormat,
            Stereo = false,
            SampleDescription = new SampleDescription(1, 0),
            Usage = Usage.RenderTargetOutput | Usage.ShaderInput,
            BufferCount = 2,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Ignore,
            Flags = flags
        };

        using var sc1 = new SwapChain1(factory2, device, hwnd, ref desc);
        var sc3 = sc1.QueryInterface<SwapChain3>();

        sc3.ColorSpace1 = HdrResources.HdrColorSpace;

        MyPlatformRender.m_swapchain = sc3;
        MyRender11.m_swapchain = sc3;
        HdrResources.SetInitialized(true);

        MyRender11.Backbuffer = new MyBackbuffer(sc3.GetBackBuffer<Texture2D>(0));

        factory2.MakeWindowAssociation(hwnd, WindowAssociationFlags.IgnoreAll);
        VRage.Utils.MyLog.Default.WriteLine("HDR: swapchain replaced with flip model fp16 scRGB");

        // If SE was started directly into Fullscreen, SE's window has been left in
        // a fullscreen-leftover state by the SetFullscreenState(false) above and
        // RestoreFullscreenMode would normally fix it on the next SetFocus event.
        // But the window is already focused at startup, so that event never fires.
        // Manually trigger it so the next ApplySettings(null)/TryChangeToFullscreen
        // restores exclusive fullscreen (and our ResizeBuffers postfix fires).
        if (MyPlatformRender.m_settings.WindowMode == MyWindowModeEnum.Fullscreen)
            MyPlatformRender.RestoreFullscreenMode();
    }

    // Re-allocates the swapchain's backbuffer in HDR format. Required after any
    // fullscreen<->windowed transition on a flip-model swapchain.
    public static void RebuildBackbuffer()
    {
        var sw = MyRender11.m_swapchain;
        if (sw == null) return;

        MyRender11.Backbuffer?.Release();
        var d = sw.Description;
        sw.ResizeBuffers(d.BufferCount, d.ModeDescription.Width, d.ModeDescription.Height,
            HdrResources.BackbufferFormat, d.Flags);
        MyRender11.Backbuffer = new MyBackbuffer(sw.GetBackBuffer<Texture2D>(0));
    }
}

[HarmonyPatch(typeof(MyPlatformRender), nameof(MyPlatformRender.CreateSwapChain), typeof(IntPtr))]
internal static class CreateSwapChainPatch
{
    private static bool Prefix() => !HdrResources.Initialized;
}

// DXGI flip model requires ResizeBuffers after SetFullscreenState(true),
// otherwise the next Present throws DXGI_ERROR_INVALID_CALL.
// (https://learn.microsoft.com/windows/win32/direct3ddxgi/for-best-performance--use-dxgi-flip-model)
// Stock SE uses a bitblt swapchain which is exempt; ReplaceSwapChain switched
// us to flip model, so this step must be added back.
[HarmonyPatch(typeof(MyPlatformRender), nameof(MyPlatformRender.TryChangeToFullscreen))]
internal static class TryChangeToFullscreenPatch
{
    private static void Postfix()
    {
        var sw = MyRender11.m_swapchain;
        if (sw == null) return;

        sw.GetFullscreenState(out var isFs, out var output);
        output?.Dispose();
        if (!isFs) return;

        SwapChainReplacer.RebuildBackbuffer();
        VRage.Utils.MyLog.Default.WriteLine("HDR: ResizeBuffers after SetFullscreenState(true)");
    }
}

// DXGI forcibly drops exclusive fullscreen on focus loss (alt-tab, Win key).
// This forced transition cannot be disabled even with DXGI_MWA_NO_ALT_ENTER.
// flip-model swapchains require ResizeBuffers after any fullscreen<->windowed
// transition, otherwise the very next Present throws DXGI_ERROR_INVALID_CALL.
// SE has no knowledge that DXGI dropped fullscreen, so we detect it ourselves
// and repair the swapchain right before SE issues Present.
[HarmonyPatch(typeof(MyRender11), nameof(MyRender11.Present))]
internal static class PresentForceExitFullscreenPatch
{
    private static bool _wasFullScreen;

    private static void Prefix()
    {
        var sw = MyRender11.m_swapchain;
        if (sw == null) return;

        sw.GetFullscreenState(out var isFs, out var fsOut);
        fsOut?.Dispose();

        if (_wasFullScreen && !isFs)
        {
            SwapChainReplacer.RebuildBackbuffer();
            VRage.Utils.MyLog.Default.WriteLine("HDR: ResizeBuffers after DXGI-forced exit fullscreen");
        }

        _wasFullScreen = isFs;
    }
}

// SE's Present call sites (MyRender11.cs:3651-3658) hard-code PresentFlags.None
// and pass m_settings.VSync as syncInterval. DXGI requires that AllowTearing
// be combined with syncInterval=0 AND PresentFlags.AllowTearing — any other
// pairing returns DXGI_ERROR_INVALID_CALL. Override both here when the feature
// is active so SE's VSync setting is bypassed for our swapchain.
//
// __instance filter: SwapChain.Present is shared by every swapchain in the
// process. Only override for the one we own; other plugins' swapchains pass
// through untouched.
[HarmonyPatch(typeof(SwapChain), nameof(SwapChain.Present), typeof(int), typeof(PresentFlags))]
internal static class SwapChainPresentTearingPatch
{
    private static void Prefix(SwapChain __instance, ref int syncInterval, ref PresentFlags flags)
    {
        if (!Config.Current.AllowTearing || !SwapChainReplacer.AllowTearingSupported) return;
        if (!ReferenceEquals(__instance, MyRender11.m_swapchain)) return;
        syncInterval = 0;
        flags |= PresentFlags.AllowTearing;
    }
}
