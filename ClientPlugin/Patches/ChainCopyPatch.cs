using ClientPlugin.Rendering;
using HarmonyLib;
using VRage.Render11.Resources;
using VRage.Render11.Resources.Internal;
using VRageRender;

namespace ClientPlugin.Patches;

// The post-process chain (MyCustomTexture) is graphics-normalized, see
// HdrTonemap.hlsl. MyRender11.DrawGameScene ends it by copying its last texture into
// the render target (backbuffer, or the large-screenshot target) with
// MyCopyToRT.Run, so that copy is where it becomes scRGB. The other callers - the
// screenshot copy of the backbuffer and the texture exporter - never pass a
// MyCustomTexture view and run the original.
[HarmonyPatch(typeof(MyCopyToRT), nameof(MyCopyToRT.Run))]
internal static class ChainCopyPatch
{
    private static bool Prefix(IRtvBindable destination, ISrvBindable source, bool alphaBlended, MyViewport? customViewport, bool shouldStretch)
    {
        if (source is not MyCustomTextureFormat)
            return true;

        OutputEncode.Draw(
            destination,
            source,
            alphaBlended ? MyBlendStateManager.BlendAlphaPremult : null,
            customViewport ?? new MyViewport(destination.Size.X, destination.Size.Y),
            filter: shouldStretch || source.Size != destination.Size);
        return false;
    }
}
