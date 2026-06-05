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
    public static VertexShader UiCompositeVertexShader { get; private set; }
    public static PixelShader UiCompositePixelShader { get; private set; }
    public static IConstantBuffer HdrConstantBuffer { get; private set; }
    public static IConstantBuffer UiConstantBuffer { get; private set; }

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
        UiCompositeVertexShader = CompileVertex(device, "UiComposite.hlsl", "vs_main");
        UiCompositePixelShader = CompilePixel(device, "UiComposite.hlsl", "ps_main");

        HdrConstantBuffer = MyManagers.Buffers.CreateConstantBuffer(
            "HdrOutput.HdrConstants",
            Utilities.SizeOf<Patches.TonemapPatch.HdrConstants>(),
            usage: ResourceUsage.Dynamic);

        UiConstantBuffer = MyManagers.Buffers.CreateConstantBuffer(
            "HdrOutput.UiConstants",
            16,
            usage: ResourceUsage.Dynamic);
    }

    public static void SetInitialized(bool value)
    {
        Initialized = value;
    }

    public static void Release()
    {
        TonemapComputeShader?.Dispose();
        TonemapComputeShader = null;
        UiCompositeVertexShader?.Dispose();
        UiCompositeVertexShader = null;
        UiCompositePixelShader?.Dispose();
        UiCompositePixelShader = null;

        if (HdrConstantBuffer != null)
        {
            MyManagers.Buffers.Dispose(HdrConstantBuffer);
            HdrConstantBuffer = null;
        }
        if (UiConstantBuffer != null)
        {
            MyManagers.Buffers.Dispose(UiConstantBuffer);
            UiConstantBuffer = null;
        }

        Initialized = false;
    }

    private static ComputeShader CompileCompute(SharpDX.Direct3D11.Device device, string file, string entry)
    {
        using var bc = Compile(file, entry, "cs_5_0");
        return new ComputeShader(device, bc);
    }

    private static VertexShader CompileVertex(SharpDX.Direct3D11.Device device, string file, string entry)
    {
        using var bc = Compile(file, entry, "vs_5_0");
        return new VertexShader(device, bc);
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
