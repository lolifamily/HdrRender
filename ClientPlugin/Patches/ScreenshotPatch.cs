using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ClientPlugin.Rendering;
using HarmonyLib;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SixLabors.ImageSharp.PixelFormats;
using VRage.Render.Image;
using VRage.Render11.Resources;
using VRageMath;
using VRageMath.PackedVector;
using VRageRender;

namespace ClientPlugin.Patches;

[HarmonyPatch(typeof(MyRender11), nameof(MyRender11.SaveScreenshotFromResource), typeof(IResource))]
internal static class ScreenshotPatch
{
    private static bool Prefix(IResource res)
    {
        if (!MyRender11.m_screenshot.HasValue)
            return false;

        var screenshot = MyRender11.m_screenshot.Value;
        var savePath = screenshot.SavePath;
        var format = screenshot.Format;
        var showNotification = screenshot.ShowNotification;
        MyRender11.m_screenshot = null;

        try
        {
            if (res.Resource is not Texture2D texture)
            {
                VRage.Utils.MyLog.Default.WriteLine("HDR: Screenshot failed - resource is not Texture2D");
                MyRenderProxy.ScreenshotTaken(false, savePath, showNotification);
                return false;
            }

            var desc = texture.Description;
            using var staging = new Texture2D(MyRender11.DeviceInstance, new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                Usage = ResourceUsage.Staging,
                SampleDescription = new SampleDescription(1, 0),
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            });

            var ctx = MyRender11.DeviceInstance.ImmediateContext;
            ctx.CopyResource(texture, staging);

            // Map/Unmap MUST be a matched pair. A 4K HDR backbuffer is ~64 MB, so
            // the byte[] allocation can OOM; Marshal.Copy can raise AccessViolation
            // on driver weirdness. Without try/finally, the staging texture would
            // leave the Map scope still in MAPPED state, and the outer `using`
            // would then Dispose a mapped resource — D3D11 debug layer error and
            // potential GPU memory leak in release runtime.
            var dataBox = ctx.MapSubresource(staging, 0, MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
            var w = desc.Width;
            var h = desc.Height;
            var rowPitch = dataBox.RowPitch;
            byte[] pixelBuffer;
            try
            {
                pixelBuffer = new byte[rowPitch * h];
                Marshal.Copy(dataBox.DataPointer, pixelBuffer, 0, pixelBuffer.Length);
            }
            finally
            {
                ctx.UnmapSubresource(staging, 0);
            }

            var paperWhite = Config.Current.PaperWhite;
            Task.Run(() => SaveInBackground(savePath, format, pixelBuffer, rowPitch, w, h, showNotification, paperWhite));
        }
        catch (Exception e)
        {
            VRage.Utils.MyLog.Default.WriteLine($"HDR: Screenshot failed: {e}");
            MyRenderProxy.ScreenshotTaken(false, savePath, showNotification);
        }

        return false;
    }

    private static unsafe void SaveInBackground(string savePath, MyImage.FileFormat format, byte[] pixelBuffer, int rowPitch, int w, int h, bool showNotification, float paperWhite)
    {
        var success = false;
        try
        {
            fixed (byte* ptr = pixelBuffer)
            {
                var dataPtr = (IntPtr)ptr;
                if (format == MyImage.FileFormat.Png)
                {
                    // Png is the user-screenshot path (F4, blueprints — the game itself
                    // never asks for anything else): keep the HDR original as EXR.
                    var exrFile = new FileInfo(Path.ChangeExtension(savePath, ".exr"));
                    exrFile.Directory?.Create();

                    Parallel.Invoke(
                        () =>
                        {
                            using var fs = exrFile.Create();
                            ExrWriter.Write(fs, dataPtr, rowPitch, w, h);
                        },
                        () => SaveSdr(savePath, format, dataPtr, rowPitch, w, h, paperWhite));

                    VRage.Utils.MyLog.Default.WriteLine($"HDR: EXR saved to {exrFile.FullName}");
                }
                else
                {
                    // Jpg/Bmp can only come from programmatic callers: honor the
                    // requested format, no EXR side product next to a temp file.
                    SaveSdr(savePath, format, dataPtr, rowPitch, w, h, paperWhite);
                }

                VRage.Utils.MyLog.Default.WriteLine($"HDR: {format} saved to {savePath}");
                success = true;
            }
        }
        catch (Exception e)
        {
            VRage.Utils.MyLog.Default.WriteLine($"HDR: Screenshot save failed: {e}");
        }
        finally
        {
            MyRenderProxy.ScreenshotTaken(success, savePath, showNotification);
        }
    }

    private static unsafe void SaveSdr(string path, MyImage.FileFormat format, IntPtr pixelData, int rowPitch, int width, int height, float paperWhite)
    {
        // BT.2446 Method A style: paper_white is the SDR "graphic white" anchor.
        // - Below knee:  pure linear pass-through of (HDR / paper_white) to preserve midtones.
        // - Above knee:  Reinhard shoulder asymptotic to 1.0 (SDR display max).
        // knee=0.75 follows BT.2408 / Adobe "75% IRE graphic white" convention.
        var paperWhiteScRGB = paperWhite / 80f;
        const float knee     = 0.75f;
        const float delta    = 0.5f;
        const float range    = 1f - knee;
        // Hunt-effect correction: desaturate input when normalized luma exceeds
        // paper_white. Same shape as hdrfix Hable: rgb_desat = lerp(rgb, luma, overbright).
        const float desatThreshold = 1.0f;

        var sdr = new byte[width * height * 4];
        var pixelDataAddr = pixelData;

        Parallel.For(0, height, y =>
        {
            var row = (ushort*)((byte*)pixelDataAddr + (long)y * rowPitch);
            var lineOff = y * width * 4;

            for (var x = 0; x < width; x++)
            {
                var px = row + x * 4; // R16G16B16A16 = 4 x ushort per pixel
                var rN = MathHelper.Max(HalfUtils.Unpack(px[0]), 0f) / paperWhiteScRGB;
                var gN = MathHelper.Max(HalfUtils.Unpack(px[1]), 0f) / paperWhiteScRGB;
                var bN = MathHelper.Max(HalfUtils.Unpack(px[2]), 0f) / paperWhiteScRGB;

                // Pre-tonemap desaturation for overbright pixels.
                var luma       = MathHelper.Max(0.2126f * rN + 0.7152f * gN + 0.0722f * bN, 1e-6f);
                var overbright = MathHelper.Saturate((luma - desatThreshold) / luma);
                rN = rN * (1f - overbright) + luma * overbright;
                gN = gN * (1f - overbright) + luma * overbright;
                bN = bN * (1f - overbright) + luma * overbright;

                var r = LinearToSrgb(ToneMap(rN, knee, range, delta));
                var g = LinearToSrgb(ToneMap(gN, knee, range, delta));
                var b = LinearToSrgb(ToneMap(bN, knee, range, delta));

                var i = lineOff + x * 4;
                sdr[i]     = (byte)PackUtils.PackUNorm(255f, r);
                sdr[i + 1] = (byte)PackUtils.PackUNorm(255f, g);
                sdr[i + 2] = (byte)PackUtils.PackUNorm(255f, b);
                sdr[i + 3] = 255;
            }
        });

        fixed (byte* ptr = sdr)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            MyImage.Save<Rgba32>(fs, format, (IntPtr)ptr, width * 4, new Vector2I(width, height), 4);
        }
    }

    private static float ToneMap(float norm, float knee, float range, float delta)
    {
        if (norm <= knee)
            return norm;
        var excess = norm - knee;
        return knee + range * excess / (excess + delta);
    }

    private static float LinearToSrgb(float c) =>
        c <= 0.0031308f ? c * 12.92f : 1.055f * (float)Math.Pow(c, 1.0 / 2.4) - 0.055f;
}
