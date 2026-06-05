using System;
using System.ComponentModel;
using System.Reflection;
using ClientPlugin.Rendering;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Layouts;
using HarmonyLib;
using JetBrains.Annotations;
using Sandbox.Graphics.GUI;
using SharpDX.DXGI;
using VRage.Plugins;
using VRageRender;

#if !DEV_BUILD
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
#endif

namespace ClientPlugin;

public class Plugin : IPlugin
{
    public const string Name = "HdrOutput";
    internal static string StatusTitle { get; private set; } = "HDR Output";
    internal static string StatusInfo { get; private set; }
    private SettingsGenerator _settingsGenerator;

    [UsedImplicitly]
    public void LoadAssets(string path)
    {
        VRage.Utils.MyLog.Default.WriteLine($"HDR: LoadAssets called with path: {path}");
        HdrResources.SetAssetsPath(path);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Init(object gameInstance)
    {
        if (Config.Current.DisabledAfterCrash)
        {
            StatusTitle = "HDR Output — Disabled (last launch crashed)";
            StatusInfo = "Auto-disabled after a crash during plugin init.\n" +
                         "Uncheck 'DisabledAfterCrash' in the config to retry on next launch.";
            VRage.Utils.MyLog.Default.WriteLine("HDR: Skipped due to DisabledAfterCrash flag");
            _settingsGenerator = new SettingsGenerator();
            return;
        }

        var shouldEnable = DetermineHdrMode();
        _settingsGenerator = new SettingsGenerator();

        if (!shouldEnable) return;

        HdrResources.InitShaders();
        Patches.SwapChainReplacer.ProbeFeatureSupport();
        Patches.SwapChainReplacer.ApplyMaximumFrameLatency();

        var harmony = new Harmony(Name);
        harmony.PatchAll(Assembly.GetExecutingAssembly());

        Config.Current.PropertyChanged += OnConfigChanged;
    }

    private static void OnConfigChanged(object sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Config.HqTarget):
                MyRenderProxy.Settings.User.HqTarget = Config.Current.HqTarget;
                MyRenderProxy.SetSettingsDirty();
                break;
            case nameof(Config.LowLatencyMode):
                Patches.SwapChainReplacer.ApplyMaximumFrameLatency();
                break;
        }
    }

    public void Dispose()
    {
        Config.Current.PropertyChanged -= OnConfigChanged;
        HdrResources.Release();
    }

    public void Update()
    {
    }

    [UsedImplicitly]
    public void OpenConfigDialog()
    {
        _settingsGenerator.SetLayout<Simple>();
        MyGuiSandbox.AddScreen(_settingsGenerator.Dialog);
    }

    private static bool DetermineHdrMode()
    {
        try
        {
            var swapchain = MyRender11.m_swapchain;
            if (swapchain == null) return false;

            var displayIsHdr = false;
            try
            {
                using var output = swapchain.ContainingOutput;
                using var output6 = output.QueryInterface<Output6>();
                var desc = output6.Description1;
                displayIsHdr = desc.ColorSpace == ColorSpaceType.RgbFullG2084NoneP2020;
                StatusInfo = $"Peak: {desc.MaxLuminance:F0} nits | Full frame: {desc.MaxFullFrameLuminance:F0} nits\n" +
                             $"Depth: {desc.BitsPerColor}-bit\n" +
                             $"Mode: {(displayIsHdr ? "HDR" : "SDR")}";
            }
            catch
            {
                StatusInfo = "Cannot read display info";
            }

            if (displayIsHdr)
            {
                StatusTitle = "HDR Output — Active";
                VRage.Utils.MyLog.Default.WriteLine($"HDR: Display HDR active. {StatusInfo}");
                return true;
            }

            if (Config.Current.ForceEnable)
            {
                StatusTitle = "HDR Output — Forced";
                VRage.Utils.MyLog.Default.WriteLine($"HDR: Force mode. {StatusInfo}");
                return true;
            }

            StatusTitle = "HDR Output — Inactive";
            VRage.Utils.MyLog.Default.WriteLine(
                $"HDR: {StatusInfo}. Set ForceEnable to override.");
            return false;
        }
        catch (Exception e)
        {
            StatusTitle = "HDR Output — Error";
            StatusInfo = e.Message;
            VRage.Utils.MyLog.Default.WriteLine($"HDR: {e.Message}");
            return false;
        }
    }
}
