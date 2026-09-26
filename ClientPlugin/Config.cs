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

    [Checkbox(description: "Force HDR on a non-HDR display. Experts only; requires restart.")]
    public bool ForceEnable
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = false;

    [Slider(200f, 2500f, 50f, description: "Display peak brightness in nits. The reported value is shown above.")]
    public float PeakBrightness
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 1000f;

    [Slider(80f, 500f, 10f, description: "Game world brightness in nits: where SDR white lands. Highlights go above it up to peak. BT.2408: 203.")]
    public float ScenePaperWhite
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 200f;

    // Serialized as <PaperWhite>: that setting only ever drove the UI brightness, so
    // existing configs already hold the UI brightness there.
    [XmlElement("PaperWhite")]
    [Slider(40f, 500f, 10f, label: "UI brightness", description: "Menus, HUD and in-world overlays (block highlight, gizmos, crosshair) in nits. Independent of paper white.")]
    public float UiBrightness
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 200f;

    [Slider(4f, 8f, 0.5f, description: "Stops above average scene brightness that keep highlight detail; brighter content sits at peak. Lower it on dim displays: 4 for 400 nits.")]
    public float HighlightRange
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 6f;

    [Slider(0f, 0.8f, 0.05f, description: "Midtones up to this share of paper white look as in SDR. 0 = off.")]
    public float VanillaMidtones
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 0.7f;

    [Slider(0f, 1f, 0.05f, description: "0 = vanilla: bright colors fade to white. 1 = they keep their color.")]
    public float NaturalColor
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 1f;

    [Slider(0f, 0.05f, 0.002f, description: "Raises near-black detail (BT.2390 black lift). 0 = off, best for OLED.")]
    public float BlackLift
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 0.0f;

    // Name kept so existing configs keep their value. These are emissive billboards and
    // GPU particles in the scene, not the engine's post-tonemap LDR billboard bucket.
    [Slider(1f, 16f, 0.5f, label: "Emissive boost", description: "Brightens thruster flames, muzzle flashes and other emissive effects. 1 = original.")]
    public float LdrIntensity
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = 1.0f;

    [Separator("Advanced")]
    [Checkbox(description: "16-bit lighting buffer: smoother HDR gradients, more VRAM.")]
    public bool HqTarget
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = true;

    [Checkbox(description: "Variable refresh rate in windowed mode. Needs G-Sync/FreeSync, otherwise tears.")]
    public bool AllowTearing
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = false;

    [Checkbox(description: "Caps the GPU frame queue at 1 for lower input latency. May stutter when GPU-bound.")]
    public bool LowLatencyMode
    {
        get;
        [UsedImplicitly]
        set => SetField(ref field, value);
    } = false;

    [Checkbox(description: "Set automatically after an init crash. Uncheck to retry HDR on next launch.")]
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