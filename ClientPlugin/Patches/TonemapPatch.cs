using System;
using System.Runtime.InteropServices;
using ClientPlugin.Rendering;
using HarmonyLib;
using SharpDX;
using SharpDX.Direct3D11;
using VRage.Render11.Common;
using VRage.Render11.Resources;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyToneMapping), nameof(MyToneMapping.Run))]
internal static class TonemapPatch
{
    // Mirrors cbuffer HdrConstants in HdrTonemap.hlsl
    [StructLayout(LayoutKind.Sequential)]
    public struct HdrConstants
    {
        public float PaperWhiteScRGB;
        public float GraphicsWhiteScRGB;
        public float PeakScRGB;
        public float SourcePeakScRGB;

        public float SdrGain;
        public float BloomMult;
        public float BloomDirtRatio;
        public float BlackLift;

        public float GrainStrength;
        public float GrainAmount;
        public int GrainSize;
        public float FrameTime;

        public float Contrast;
        public float Brightness;
        public float Saturation;
        public float Vibrance;

        public float BrightnessFactorR;
        public float BrightnessFactorG;
        public float BrightnessFactorB;
        public float SepiaStrength;

        public float LightColorR;
        public float LightColorG;
        public float LightColorB;
        public int DisableTonemapping;

        public float DarkColorR;
        public float DarkColorG;
        public float DarkColorB;
        public int NeedsAlphaLuminance;
    }

    // Slope at black of the engine's Hable curve (Filters.hlsli) over its value at the
    // white point: the fraction of SDR white one exposed LBuffer unit maps to in the
    // engine's shadows and midtones. 0.383 at the stock WhitePoint of 11.2.
    private static float SdrGainAt(float whitePoint)
    {
        const float a = 0.15f, b = 0.50f, c = 0.10f, d = 0.20f, e = 0.02f, f = 0.30f;
        var w = Math.Max(whitePoint, 1e-3f);
        var hableW = (w * (a * w + c * b) + d * e) / (w * (a * w + b) + d * f) - e / f;
        return b * (c * f - e) / (d * f * f) / hableW;
    }

    private static bool Prefix(
        ISrvBindable src,
        ISrvBindable avgLum,
        ISrvBindable bloom,
        bool enableTonemapping,
        string dirtTexture,
        bool needsAlphaLuminance,
        ref IBorrowedCustomTexture __result)
    {
        var dest = MyManagers.RwTexturesPool.BorrowCustom("DrawGameScene.Tonemapped");
        var rc = MyImmediateRC.RC;
        var ctx = MyRender11.DeviceInstance.ImmediateContext;

        var cfg = Config.Current;
        ref var pp = ref MyRender11.Postprocess;
        var paperWhite = cfg.ScenePaperWhite / 80f;
        var peak = cfg.PeakBrightness / 80f;
        var sdrGain = SdrGainAt(pp.Data.WhitePoint);
        var constants = new HdrConstants
        {
            PaperWhiteScRGB = paperWhite,
            GraphicsWhiteScRGB = cfg.UiBrightness / 80f,
            PeakScRGB = peak,
            // Scene content HighlightRange stops above the exposure reference reaches peak:
            // sdrGain * 2^range scene-normalized, times paper white for scRGB. Never below
            // peak - BT.2390 would hard-clip there and waste the headroom above it.
            SourcePeakScRGB = Math.Max(sdrGain * (float)Math.Pow(2, cfg.HighlightRange) * paperWhite, peak),

            SdrGain = sdrGain,
            BloomMult = pp.Data.BloomMult,
            BloomDirtRatio = pp.Data.BloomDirtRatio,
            BlackLift = cfg.BlackLift,

            GrainStrength = pp.Data.GrainStrength,
            GrainAmount = pp.Data.GrainAmount,
            GrainSize = pp.Data.GrainSize,
            FrameTime = MyCommon.FrameConstantsData.FrameTime,

            Contrast = pp.Data.Contrast,
            Brightness = pp.Data.Brightness,
            Saturation = pp.Data.Saturation,
            Vibrance = pp.Data.Vibrance,

            BrightnessFactorR = pp.Data.BrightnessFactorR,
            BrightnessFactorG = pp.Data.BrightnessFactorG,
            BrightnessFactorB = pp.Data.BrightnessFactorB,
            SepiaStrength = pp.Data.SepiaStrength,

            LightColorR = pp.Data.LightColor.X,
            LightColorG = pp.Data.LightColor.Y,
            LightColorB = pp.Data.LightColor.Z,
            DisableTonemapping = enableTonemapping ? 0 : 1,

            DarkColorR = pp.Data.DarkColor.X,
            DarkColorG = pp.Data.DarkColor.Y,
            DarkColorB = pp.Data.DarkColor.Z,
            NeedsAlphaLuminance = needsAlphaLuminance ? 1 : 0
        };

        var mapped = ctx.MapSubresource(HdrResources.HdrConstantBuffer.Resource, 0, MapMode.WriteDiscard, MapFlags.None);
        Utilities.Write(mapped.DataPointer, ref constants);
        ctx.UnmapSubresource(HdrResources.HdrConstantBuffer.Resource, 0);

        rc.ComputeShader.SetConstantBuffer(0, HdrResources.HdrConstantBuffer);
        rc.ComputeShader.SetUav(0, dest);

        var dirt = MyManagers.Textures.GetTempTexture(dirtTexture,
            new VRage.Render11.Resources.Textures.MyTextureStreamingManager.QueryArgs
            {
                TextureType = MyFileTextureEnum.ALPHAMASK,
                WaitUntilLoaded = true,
                SkipQualityReduction = true
            });

        rc.ComputeShader.SetSrvs(0, src, avgLum, bloom, dirt);
        rc.ComputeShader.SetSampler(0, MySamplerStateManager.Default);

        rc.ComputeShader.Set(HdrResources.TonemapComputeShader);

        var size = dest.Size;
        rc.Dispatch((size.X + 7) / 8, (size.Y + 7) / 8, 1);

        rc.ComputeShader.SetUav(0, null);
        rc.ComputeShader.Set(null);
        // The engine's pass leaves its frame constants bound to b0
        rc.ComputeShader.SetConstantBuffer(0, MyCommon.FrameConstants);

        __result = dest;
        return false;
    }
}
