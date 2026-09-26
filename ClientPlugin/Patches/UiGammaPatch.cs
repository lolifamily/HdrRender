using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ClientPlugin.Rendering;
using HarmonyLib;
using SharpDX.Direct3D11;
using VRage.Render11.Common;
using VRage.Render11.RenderContext;
using VRage.Render11.Resources;
using VRage.Render11.Sprites;

namespace ClientPlugin.Patches;

// The UI as a gamma 2.2 monitor shows it, like the scene (HdrTonemap.hlsl sdr_on_gamma22).
//
// The engine's sprite pixel shaders output linear light into its sRGB backbuffer, which the monitor decoded with 2.2.
// On the scRGB render target that linear light is what reaches the screen, and dark UI came out lighter than it was
// drawn. UiSprites.hlsl is the engine's sprite pixel shader with the 2.2 decode: every rc.PixelShader.Set in
// MySpritesManager.Render goes through SetPixelShader below, which swaps it in for the engine's own while Render draws
// into an scRGB target. LCD screens and the other offscreen sprite targets keep the engine's shaders.
//
// A transpiler for the same reason as UiBlendPatch: the shader is set in Render, which never passes the target on.
[HarmonyPatch(typeof(MySpritesManager), nameof(MySpritesManager.Render))]
internal static class UiGammaPatch
{
    private static readonly MethodInfo EngineSet = AccessTools.Method(typeof(MyPixelStage),
        nameof(MyPixelStage.Set), [typeof(PixelShader)]);

    private static readonly MethodInfo UiSet = AccessTools.Method(typeof(UiGammaPatch), nameof(SetPixelShader));

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var codes = new List<CodeInstruction>(instructions);
        var rtvIndex = Array.FindIndex(original.GetParameters(), p => p.ParameterType == typeof(IRtvBindable));
        var replaced = 0;
        for (var i = 0; rtvIndex >= 0 && i < codes.Count; i++)
        {
            if (!codes[i].Calls(EngineSet))
                continue;

            // Stack: stage, shader. Push the target too and call ours instead, as UiBlendPatch does.
            var push = CodeInstruction.LoadArgument(rtvIndex + 1); // arg 0 is this
            push.labels.AddRange(codes[i].labels);
            push.blocks.AddRange(codes[i].blocks);
            codes[i] = new CodeInstruction(OpCodes.Call, UiSet);
            codes.Insert(i++, push);
            replaced++;
        }

        if (replaced == 0)
            VRage.Utils.MyLog.Default.WriteLine(
                "HDR: MySpritesManager.Render: no PixelShader.Set call to hook -> UI without gamma 2.2 (engine layout changed?)");
        return codes;
    }

    private static void SetPixelShader(MyPixelStage stage, PixelShader shader, IRtvBindable rtv)
    {
        if (UiBlendPatch.IsScRgb(rtv))
        {
            var sprites = MyManagers.SpritesManager;
            if (shader == (PixelShader)sprites.m_ps)
                shader = HdrResources.UiSpritesPixelShader ?? shader;
            else if (shader == (PixelShader)sprites.m_psPM)
                shader = HdrResources.UiSpritesPmPixelShader ?? shader;
        }

        stage.Set(shader);
    }
}
