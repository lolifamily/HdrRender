using SharpDX;
using SharpDX.Direct3D11;
using VRage.Render11.Common;
using VRage.Render11.Resources;
using VRageRender;

namespace ClientPlugin.Rendering;

// Graphics-normalized (1.0 = UI brightness) -> scRGB, see OutputEncode.hlsl. The one
// place the UI brightness scale is applied: the post-process chain copy and the UI
// composite both draw through here.
internal static class OutputEncode
{
    // filter: resample instead of copying texel for texel, which MyCopyToRT.Run does
    // for a size mismatch or a DRS stretch.
    public static void Draw(IRtvBindable target, ISrvBindable source, IBlendState blend, MyViewport viewport, bool filter)
    {
        var graphicsWhite = Config.Current.UiBrightness / 80f;
        var ctx = MyRender11.DeviceInstance.ImmediateContext;
        var mapped = ctx.MapSubresource(HdrResources.OutputConstantBuffer.Resource, 0, MapMode.WriteDiscard, MapFlags.None);
        Utilities.Write(mapped.DataPointer, ref graphicsWhite);
        ctx.UnmapSubresource(HdrResources.OutputConstantBuffer.Resource, 0);

        var rc = MyImmediateRC.RC;
        rc.SetBlendState(blend);
        rc.SetDepthStencilState(MyDepthStencilStateManager.IgnoreDepthStencil);
        rc.SetRtv(target);
        rc.PixelShader.Set(filter ? HdrResources.OutputFilterPixelShader : HdrResources.OutputCopyPixelShader);
        rc.PixelShader.SetConstantBuffer(0, HdrResources.OutputConstantBuffer);
        rc.PixelShader.SetSrv(0, source);
        rc.PixelShader.SetSampler(2, MySamplerStateManager.Linear);
        MyScreenPass.DrawFullscreenQuad(rc, viewport);

        rc.PixelShader.SetSrv(0, null);
        // Engine passes expect their frame constants in b0
        rc.PixelShader.SetConstantBuffer(0, MyCommon.FrameConstants);
    }
}
