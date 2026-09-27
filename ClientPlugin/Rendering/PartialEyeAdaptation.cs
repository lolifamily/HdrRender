using System;
using System.Collections.Generic;
using System.Globalization;
using SharpDX.Direct3D;
using VRageRender;

namespace ClientPlugin.Rendering;

// Partial eye adaptation. MyEyeAdaptation.Run draws the engine's exposure state with
// m_eyeAdaptationShader; that pass is swapped for PartialEyeAdaptation.hlsl, the same
// pass with the exposure it adapts toward bent. Everything that applies or compensates
// the exposure (bloom, tonemap, billboards) reads that state and stays as it is.
//
// The engine builds the pass like its own shaders: a rooted path passes through its
// Path.Combine with the shaders folder, and the file's <...> includes resolve there.
internal static class PartialEyeAdaptation
{
    private const string EngineShader = "Postprocess/EyeAdaptation/EyeAdaptation.hlsl";

    // Per adaptation and environment, the pass: the engine's own for an adaptation of 1
    // and for a build that failed, which is not retried.
    private static readonly Dictionary<(float Adaptation, float ConstantLuminance), MyPixelShaders.Id> Passes = [];

    // Found by its key: MyEyeAdaptation.Init created it.
    private static MyPixelShaders.Id Engine => MyPixelShaders.Create(EngineShader);

    // Once per frame (TonemapPatch), after this frame's MyEyeAdaptation.Run: the pass set
    // here draws from the next. With eye adaptation off the engine draws its constant
    // exposure instead, and there is no pass to pick or build.
    public static void Update()
    {
        ref var pp = ref MyRender11.Postprocess;
        if (!pp.EnableEyeAdaptation)
            return;

        // Rounded to the slider's steps: each value is a build of its own.
        var key = (Adaptation: (float)Math.Round(Config.Current.EyeAdaptation, 2), pp.Data.ConstantLuminance);
        if (!Passes.TryGetValue(key, out var pass))
            Passes[key] = pass = key.Adaptation >= 1f ? Engine : Build(key.Adaptation, key.ConstantLuminance);
        MyEyeAdaptation.m_eyeAdaptationShader = pass;
    }

    // On the render thread, as the engine builds its own: 0.1 s the first time, from its
    // disk cache after. The compiler runs alone first: on an error MyPixelShaders.Create
    // stops in Debugger.Break before it throws.
    private static MyPixelShaders.Id Build(float adaptation, float constantLuminance)
    {
        var file = HdrResources.ShaderFile("PartialEyeAdaptation.hlsl");
        ShaderMacro[] macros =
        [
            new("ADAPTATION", adaptation.ToString("R", CultureInfo.InvariantCulture)),
            new("CONSTANT_LUMINANCE", constantLuminance.ToString("R", CultureInfo.InvariantCulture))
        ];

        string error;
        try
        {
            if (MyShaderCompiler.Compile(file, macros, MyShaderProfile.ps_5_0, file, optimize: false,
                                         invalidateCache: false, out _, out error, out _,
                                         savePdb: false, savePreprocessed: false) != null)
                return MyPixelShaders.Create(file, macros); // from the cache the compile just filled
        }
        catch (Exception e)
        {
            error = e.Message;
        }

        VRage.Utils.MyLog.Default.WriteLine($"HDR: eye adaptation {adaptation} unavailable, the engine's stays: {error}");
        return Engine;
    }
}
