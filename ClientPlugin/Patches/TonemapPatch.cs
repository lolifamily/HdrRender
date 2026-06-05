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
    [StructLayout(LayoutKind.Sequential)]
    public struct HdrConstants
    {
        public float PaperWhiteScRGB;
        public float PeakScRGB;
        public float SourcePeakScRGB;
        public float BloomMult;

        public float BloomDirtRatio;
        public float GrainStrength;
        public float GrainAmount;
        public int GrainSize;

        public float Contrast;
        public float Brightness;
        public float Saturation;
        public float BrightnessFactorR;

        public float BrightnessFactorG;
        public float BrightnessFactorB;
        public float Vibrance;
        public float SepiaStrength;

        public float LightColorR;
        public float LightColorG;
        public float LightColorB;
        public float FrameTime;

        public float DarkColorR;
        public float DarkColorG;
        public float DarkColorB;
        public float BlackLift;

        public int DisablePostprocess;
        public int NeedsAlphaLuminance;
        public int Pad0;
        public int Pad1;
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
        var constants = new HdrConstants
        {
            PaperWhiteScRGB = cfg.PaperWhite / 80f,
            PeakScRGB = cfg.PeakBrightness / 80f,
            SourcePeakScRGB = cfg.SourcePeak / 80f,
            BloomMult = pp.Data.BloomMult,

            BloomDirtRatio = pp.Data.BloomDirtRatio,
            GrainStrength = pp.Data.GrainStrength,
            GrainAmount = pp.Data.GrainAmount,
            GrainSize = pp.Data.GrainSize,

            Contrast = pp.Data.Contrast,
            Brightness = pp.Data.Brightness,
            Saturation = pp.Data.Saturation,
            BrightnessFactorR = pp.Data.BrightnessFactorR,

            BrightnessFactorG = pp.Data.BrightnessFactorG,
            BrightnessFactorB = pp.Data.BrightnessFactorB,
            Vibrance = pp.Data.Vibrance,
            SepiaStrength = pp.Data.SepiaStrength,

            LightColorR = pp.Data.LightColor.X,
            LightColorG = pp.Data.LightColor.Y,
            LightColorB = pp.Data.LightColor.Z,
            FrameTime = MyCommon.FrameConstantsData.FrameTime,

            DarkColorR = pp.Data.DarkColor.X,
            DarkColorG = pp.Data.DarkColor.Y,
            DarkColorB = pp.Data.DarkColor.Z,
            BlackLift = cfg.BlackLift,

            DisablePostprocess = enableTonemapping ? 0 : 1,
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

        __result = dest;
        return false;
    }
}
