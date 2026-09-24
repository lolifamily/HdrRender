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

// Replaces the engine's chromatic aberration / vignette pass, whose shader writes
// the FP16 post-process chain through a UAV declared unorm - undefined in D3D11,
// see ChromaticAberration.hlsl. Same inputs, same math, float UAV.
[HarmonyPatch(typeof(MyChromaticAberration), nameof(MyChromaticAberration.Run))]
internal static class ChromaticAberrationPatch
{
    [StructLayout(LayoutKind.Sequential)]
    public struct ChromaticConstants
    {
        public float ChromaticFactor;
        public float VignetteStart;
        public float VignetteLength;
        public int Iterations;
    }

    private static bool Prefix(IUavBindable dst, ISrvTexture src)
    {
        ref var pp = ref MyRender11.Postprocess;
        var constants = new ChromaticConstants
        {
            ChromaticFactor = pp.Data.ChromaticFactor,
            VignetteStart = pp.Data.VignetteStart,
            VignetteLength = pp.Data.VignetteLength,
            // Same clamp MyChromaticAberration.Init applies to its ITERATIONS macro
            Iterations = Math.Max(1, Math.Min(16, pp.ChromaticIterations))
        };

        var ctx = MyRender11.DeviceInstance.ImmediateContext;
        var mapped = ctx.MapSubresource(HdrResources.ChromaticConstantBuffer.Resource, 0, MapMode.WriteDiscard, MapFlags.None);
        Utilities.Write(mapped.DataPointer, ref constants);
        ctx.UnmapSubresource(HdrResources.ChromaticConstantBuffer.Resource, 0);

        var rc = MyImmediateRC.RC;
        rc.ComputeShader.SetConstantBuffer(0, HdrResources.ChromaticConstantBuffer);
        rc.ComputeShader.SetUav(0, dst);
        rc.ComputeShader.SetSrv(0, src);
        rc.ComputeShader.SetSampler(0, MySamplerStateManager.Default);
        rc.ComputeShader.Set(HdrResources.ChromaticAberrationComputeShader);

        var size = dst.Size;
        rc.Dispatch((size.X + 7) / 8, (size.Y + 7) / 8, 1);

        rc.ComputeShader.SetUav(0, null);
        rc.ComputeShader.Set(null);
        // The engine's pass leaves its frame constants bound to b0
        rc.ComputeShader.SetConstantBuffer(0, MyCommon.FrameConstants);
        return false;
    }
}
