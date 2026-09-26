using System;
using System.IO;
using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using VRage.Render11.Common;
using VRage.Render11.Resources;
using VRageRender;

namespace ClientPlugin.Rendering;

internal static class HdrResources
{
    public const Format BackbufferFormat = Format.R16G16B16A16_Float;
    public const ColorSpaceType HdrColorSpace = ColorSpaceType.RgbFullG10NoneP709;

    public static bool Initialized { get; private set; }

    public static ComputeShader TonemapComputeShader { get; private set; }
    public static ComputeShader ChromaticAberrationComputeShader { get; private set; }
    public static PixelShader OutputCopyPixelShader { get; private set; }
    public static PixelShader OutputFilterPixelShader { get; private set; }

    // The engine's two sprite pixel shaders with the gamma 2.2 decode, see UiGammaPatch.
    public static PixelShader UiSpritesPixelShader { get; private set; }
    public static PixelShader UiSpritesPmPixelShader { get; private set; }

    public static IConstantBuffer HdrConstantBuffer { get; private set; }
    public static IConstantBuffer ChromaticConstantBuffer { get; private set; }
    public static IConstantBuffer OutputConstantBuffer { get; private set; }

    // The engine's BlendAlphaPremult with rgb scaled by the blend factor, see UiBlendPatch.
    // Owned by the engine's blend state manager, which recreates it on a device reset.
    public static IBlendState UiBlendState { get; private set; }

    private static string _shadersPath;

    public static void SetAssetsPath(string folder)
    {
        _shadersPath = Path.Combine(folder, "Shaders");
    }

    public static void InitShaders()
    {
        if (TonemapComputeShader != null)
            return;

        VRage.Utils.MyLog.Default.WriteLine($"HDR: InitShaders, _shadersPath={_shadersPath ?? "null"}");

        var device = MyRender11.DeviceInstance;

        TonemapComputeShader = CompileCompute(device, "HdrTonemap.hlsl", "cs_main");
        ChromaticAberrationComputeShader = CompileCompute(device, "ChromaticAberration.hlsl", "cs_main");
        OutputCopyPixelShader = CompilePixel(device, "OutputEncode.hlsl", "ps_copy");
        OutputFilterPixelShader = CompilePixel(device, "OutputEncode.hlsl", "ps_filter");
        UiSpritesPixelShader = CompilePixel(device, "UiSprites.hlsl", "ps_sprites");
        UiSpritesPmPixelShader = CompilePixel(device, "UiSprites.hlsl", "ps_sprites_pm");

        HdrConstantBuffer = MyManagers.Buffers.CreateConstantBuffer(
            "HdrOutput.HdrConstants",
            Utilities.SizeOf<Patches.TonemapPatch.HdrConstants>(),
            usage: ResourceUsage.Dynamic);

        ChromaticConstantBuffer = MyManagers.Buffers.CreateConstantBuffer(
            "HdrOutput.ChromaticConstants",
            Utilities.SizeOf<Patches.ChromaticAberrationPatch.ChromaticConstants>(),
            usage: ResourceUsage.Dynamic);

        OutputConstantBuffer = MyManagers.Buffers.CreateConstantBuffer(
            "HdrOutput.OutputConstants",
            16,
            usage: ResourceUsage.Dynamic);

        var uiBlend = default(BlendStateDescription);
        uiBlend.RenderTarget[0].IsBlendEnabled = true;
        uiBlend.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
        uiBlend.RenderTarget[0].BlendOperation = BlendOperation.Add;
        uiBlend.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
        uiBlend.RenderTarget[0].SourceBlend = BlendOption.BlendFactor;
        uiBlend.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
        uiBlend.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
        uiBlend.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;
        UiBlendState = MyManagers.BlendStates.CreateResource("HdrOutput.UiBlend", ref uiBlend);
    }

    public static void SetInitialized(bool value)
    {
        Initialized = value;
    }

    public static void Release()
    {
        TonemapComputeShader?.Dispose();
        TonemapComputeShader = null;
        ChromaticAberrationComputeShader?.Dispose();
        ChromaticAberrationComputeShader = null;
        OutputCopyPixelShader?.Dispose();
        OutputCopyPixelShader = null;
        OutputFilterPixelShader?.Dispose();
        OutputFilterPixelShader = null;
        UiSpritesPixelShader?.Dispose();
        UiSpritesPixelShader = null;
        UiSpritesPmPixelShader?.Dispose();
        UiSpritesPmPixelShader = null;

        if (HdrConstantBuffer != null)
        {
            MyManagers.Buffers.Dispose(HdrConstantBuffer);
            HdrConstantBuffer = null;
        }
        if (ChromaticConstantBuffer != null)
        {
            MyManagers.Buffers.Dispose(ChromaticConstantBuffer);
            ChromaticConstantBuffer = null;
        }
        if (OutputConstantBuffer != null)
        {
            MyManagers.Buffers.Dispose(OutputConstantBuffer);
            OutputConstantBuffer = null;
        }

        Initialized = false;
    }

    private static ComputeShader CompileCompute(SharpDX.Direct3D11.Device device, string file, string entry)
    {
        using var bc = Compile(file, entry, "cs_5_0");
        return new ComputeShader(device, bc);
    }

    private static PixelShader CompilePixel(SharpDX.Direct3D11.Device device, string file, string entry)
    {
        using var bc = Compile(file, entry, "ps_5_0");
        return new PixelShader(device, bc);
    }

    // ShaderBytecode.Compile does NOT throw on hlsl syntax errors - HRESULT-failure
    // with a non-null errorMsgs blob is reported in-band as CompilationResult with
    // Bytecode=null and the D3DCompile output in Message. Log + throw both: log so
    // the compiler diagnostic survives even if upstream swallows the exception
    // (same pattern as MyShaderCompiler.cs:241-243).
    private static CompilationResult Compile(string file, string entry, string profile)
    {
        var src = File.ReadAllText(Path.Combine(_shadersPath, file));
        var bc = ShaderBytecode.Compile(src, entry, profile, ShaderFlags.OptimizationLevel3);
        if (bc.Bytecode != null) return bc;
        
        var msg = $"HDR: compile failed {file} ({entry}, {profile}):\n{bc.Message}";
        VRage.Utils.MyLog.Default.WriteLine(msg);
        throw new Exception(msg);
    }
}
