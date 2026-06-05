using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Serialization;
using ClientPlugin.Settings;
using ClientPlugin.Settings.Elements;
using JetBrains.Annotations;

namespace ClientPlugin;

[AttributeUsage(AttributeTargets.Property)]
internal class LabelAttribute : Attribute, IElement
{
    public List<Type> SupportedTypes { get; } = [typeof(string)];

    public List<Control> GetControls(string name, Func<object> getter, Action<object> setter)
    {
        var label = new Sandbox.Graphics.GUI.MyGuiControlLabel(text: (string)(getter() ?? ""))
        {
            ColorMask = new VRageMath.Color(180, 180, 180)
        };
        return [new Control(label)];
    }
}

public class Config : INotifyPropertyChanged
{
    // ReSharper disable once MemberCanBeMadeStatic.Global
    [XmlIgnore]
    public string Title => Plugin.StatusTitle;

    [Separator("HDR Settings")]
    [Label]
    [XmlIgnore]
    [UsedImplicitly]
    public string DisplayInfo => Plugin.StatusInfo ?? "";

    [Checkbox(description: "Force HDR on non-HDR displays — requires restart (you know what you're doing!)")]
    public bool ForceEnable
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = false;

    [Slider(200f, 2500f, 50f, description: "Display peak brightness in nits")]
    public float PeakBrightness
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 1000f;

    [Slider(80f, 500f, 10f, description: "UI / paper-white brightness in nits (sRGB reference white)")]
    public float PaperWhite
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 200f;

    [Slider(1000f, 10000f, 100f, description: "Expected scene peak luminance in nits (BT.2390 source peak). KS auto-derived from display/source ratio. Default 5000 fits SE suns / engine plumes. Set lower for SDR-like content, higher to preserve more highlight detail above peak.")]
    public float SourcePeak
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 5000f;

    [Slider(0f, 0.05f, 0.002f, description: "Black floor lift (BT.2390 black boost, now in PQ space - standard range). Raises near-black detail. 0 = off (recommended for OLED real black).")]
    public float BlackLift
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 0.0f;

    [Slider(1f, 16f, 0.5f, description: "Emissive LDR billboard boost (thrusters, lasers, muzzle flashes). Diffuse / UI unaffected. 1.0 = original look.")]
    public float LdrIntensity
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 1.0f;

    [Separator("Advanced")]
    [Checkbox(description: "Use R16G16B16A16 lighting buffer (doubles LBuffer/Bloom VRAM, sharper gradients for HDR)")]
    public bool HqTarget
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = true;

    [Checkbox(description: "Enable variable refresh rate in windowed mode — requires restart. Needs G-Sync/FreeSync display, otherwise causes visible tearing. Auto-disabled if driver lacks support.")]
    public bool AllowTearing
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = false;

    [Checkbox(description: "Lower input latency by capping GPU frame queue to 1 (default 3). May cause stutter when GPU is bottlenecked (large grids, dense effects).")]
    public bool LowLatencyMode
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = false;

    [Checkbox(description: "Auto-set when the plugin crashes during first-frame init (swapchain replacement, shader load, etc). Uncheck to retry HDR on next launch.")]
    public bool DisabledAfterCrash
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = false;

    #region Property change notification boilerplate

    public static readonly Config Default = new();
    public static readonly Config Current = ConfigStorage.Load();

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    #endregion
}