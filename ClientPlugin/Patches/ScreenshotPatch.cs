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
        var path = screenshot.SavePath;
        var showNotification = screenshot.ShowNotification;
        MyRender11.m_screenshot = null;

        try
        {
            if (res.Resource is not Texture2D texture)
            {
                VRage.Utils.MyLog.Default.WriteLine("HDR: Screenshot failed - resource is not Texture2D");
                MyRenderProxy.ScreenshotTaken(false, path, showNotification);
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

            // Settings the frame was rendered with, read on the render thread
            var paperWhite = Config.Current.ScenePaperWhite;
            var peak = Config.Current.PeakBrightness;
            Task.Run(() => SaveInBackground(screenshot, pixelBuffer, rowPitch, w, h, paperWhite, peak));
        }
        catch (Exception e)
        {
            VRage.Utils.MyLog.Default.WriteLine($"HDR: Screenshot failed: {e}");
            MyRenderProxy.ScreenshotTaken(false, path, showNotification);
        }

        return false;
    }

    private static unsafe void SaveInBackground(MyScreenshot screenshot, byte[] pixelBuffer, int rowPitch, int w, int h, float paperWhite, float peak)
    {
        var path = screenshot.SavePath;
        var success = false;
        try
        {
            fixed (byte* ptr = pixelBuffer)
            {
                var dataPtr = (IntPtr)ptr;
                new FileInfo(path).Directory?.Create();

                // The EXR is for the player's own screenshots, the ones that notify.
                // World save / blueprint / grid thumbnails and recording frames pass
                // showNotification: false and get only the image they asked for.
                var exrPath = screenshot.ShowNotification ? Path.ChangeExtension(path, ".exr") : null;

                Parallel.Invoke(
                    () =>
                    {
                        if (exrPath == null)
                            return;
                        using var fs = File.Create(exrPath);
                        ExrWriter.Write(fs, dataPtr, rowPitch, w, h);
                    },
                    () => SaveSdr(path, screenshot.Format, dataPtr, rowPitch, w, h, paperWhite, peak));

                if (exrPath != null)
                    VRage.Utils.MyLog.Default.WriteLine($"HDR: EXR saved to {exrPath}");
                VRage.Utils.MyLog.Default.WriteLine($"HDR: SDR saved to {path}");
                success = true;
            }
        }
        catch (Exception e)
        {
            VRage.Utils.MyLog.Default.WriteLine($"HDR: Screenshot save failed: {e}");
        }
        finally
        {
            MyRenderProxy.ScreenshotTaken(success, path, screenshot.ShowNotification);
        }
    }

    // SDR preview of the HDR frame, in the frame's own terms. Normalized to the scene
    // paper white, so that display setting drops out; everything else keeps its
    // relation to it, and a UI dimmer than the scene stays dimmer. Like the HDR
    // display mapping, the curve runs on max(R,G,B) and scales all channels together,
    // which keeps hue and saturation. Below half the scene white nothing changes;
    // above it an extended Reinhard shoulder, C1 at the knee, reaches 1.0 exactly at
    // the HDR peak, so the whole SDR range is used.
    private static unsafe void SaveSdr(string path, MyImage.FileFormat format, IntPtr pixelData, int rowPitch, int width, int height, float paperWhite, float peak)
    {
        const float knee = 0.5f;
        var paperWhiteScRGB = paperWhite / 80f;
        // Shoulder input at the HDR peak. Only a UI set brighter than the peak goes
        // past it, and clips at 1.0.
        var whitePoint = Math.Max((peak / paperWhite - knee) / (1f - knee), 1e-3f);
        var invWhiteSq = 1f / (whitePoint * whitePoint);

        var sdr = new byte[width * height * 4];
        var pixelDataAddr = pixelData;

        Parallel.For(0, height, y =>
        {
            var row = (ushort*)((byte*)pixelDataAddr + (long)y * rowPitch);
            var lineOff = y * width * 4;

            for (var x = 0; x < width; x++)
            {
                var px = row + x * 4; // R16G16B16A16 = 4 x ushort per pixel
                var r = MathHelper.Max(HalfUtils.Unpack(px[0]), 0f) / paperWhiteScRGB;
                var g = MathHelper.Max(HalfUtils.Unpack(px[1]), 0f) / paperWhiteScRGB;
                var b = MathHelper.Max(HalfUtils.Unpack(px[2]), 0f) / paperWhiteScRGB;

                var m = Math.Max(r, Math.Max(g, b));
                if (m > knee)
                {
                    var e = (m - knee) / (1f - knee);
                    var scale = (knee + (1f - knee) * e * (1f + e * invWhiteSq) / (1f + e)) / m;
                    r *= scale;
                    g *= scale;
                    b *= scale;
                }

                var i = lineOff + x * 4;
                sdr[i]     = (byte)PackUtils.PackUNorm(255f, LinearToSrgb(Math.Min(r, 1f)));
                sdr[i + 1] = (byte)PackUtils.PackUNorm(255f, LinearToSrgb(Math.Min(g, 1f)));
                sdr[i + 2] = (byte)PackUtils.PackUNorm(255f, LinearToSrgb(Math.Min(b, 1f)));
                sdr[i + 3] = 255;
            }
        });

        fixed (byte* ptr = sdr)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            MyImage.Save<Rgba32>(fs, format, (IntPtr)ptr, width * 4, new Vector2I(width, height), 4);
        }
    }

    private static float LinearToSrgb(float c) =>
        c <= 0.0031308f ? c * 12.92f : 1.055f * (float)Math.Pow(c, 1.0 / 2.4) - 0.055f;
}
