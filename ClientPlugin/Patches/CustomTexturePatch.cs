using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using SharpDX.DXGI;
using VRage.Render11.Resources.Internal;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyCustomTexture))]
internal static class CustomTextureFormatPatch
{
    private static IEnumerable<CodeInstruction> Upgrade(IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        for (var i = 0; i < list.Count - 1; i++)
        {
            if (!IsRgba8(list[i])) continue;
            if (!ConsumedAsFormat(list[i + 1])) continue;

            list[i] = new CodeInstruction(OpCodes.Ldc_I4, (int)Format.R16G16B16A16_Float)
                { labels = list[i].labels, blocks = list[i].blocks };
        }
        return list;
    }

    private static bool IsRgba8(CodeInstruction ci) =>
        ci.LoadsConstant((int)Format.R8G8B8A8_Typeless)  ||
        ci.LoadsConstant((int)Format.R8G8B8A8_UNorm)     ||
        ci.LoadsConstant((int)Format.R8G8B8A8_UNorm_SRgb);

    private static bool ConsumedAsFormat(CodeInstruction next) => next.operand switch
    {
        FieldInfo fi => fi.FieldType == typeof(Format),
        MethodBase mb when mb.GetParameters() is { Length: > 0 } ps
            => ps[ps.Length - 1].ParameterType == typeof(Format),
        _ => false
    };

    [HarmonyTranspiler, HarmonyPatch(nameof(MyCustomTexture.Init))]
    private static IEnumerable<CodeInstruction> InitTp(IEnumerable<CodeInstruction> il) => Upgrade(il);

    [HarmonyTranspiler, HarmonyPatch(nameof(MyCustomTexture.OnDeviceInit))]
    private static IEnumerable<CodeInstruction> OnDeviceInitTp(IEnumerable<CodeInstruction> il) => Upgrade(il);
}
