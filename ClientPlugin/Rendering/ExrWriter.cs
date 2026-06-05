using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

namespace ClientPlugin.Rendering;

internal static class ExrWriter
{
    private const int ZipBlockLines = 16;
    private const int ChannelCount = 3; // B, G, R
    private const int HalfSize = 2;
    private const int SrcPixelStride = 8; // R16G16B16A16 = 4 halfs = 8 bytes

    public static void Write(Stream output, IntPtr pixelData, int rowPitch, int width, int height)
    {
        using var bw = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);

        WriteHeader(bw, width, height);

        var chunkCount = (height + ZipBlockLines - 1) / ZipBlockLines;
        var offsetTablePos = output.Position;
        for (var i = 0; i < chunkCount; i++)
            bw.Write((long)0);

        var offsets = new long[chunkCount];
        var bytesPerChannel = width * HalfSize;
        var rawLineSize = bytesPerChannel * ChannelCount;

        // Stage 1: each chunk's ExtractBgr+ZlibCompress is independent. Parallelize.
        var compressedChunks = new byte[chunkCount][];
        Parallel.For(0, chunkCount, chunk =>
        {
            var yStart = chunk * ZipBlockLines;
            var linesInChunk = Math.Min(ZipBlockLines, height - yStart);
            var raw = new byte[linesInChunk * rawLineSize];
            ExtractBgr(pixelData, rowPitch, width, yStart, linesInChunk, raw);
            compressedChunks[chunk] = ZlibCompress(raw);
        });

        // Stage 2: must write chunks in order so stream offsets are correct.
        for (var chunk = 0; chunk < chunkCount; chunk++)
        {
            offsets[chunk] = output.Position;
            var compressed = compressedChunks[chunk];
            bw.Write(chunk * ZipBlockLines);
            bw.Write(compressed.Length);
            bw.Write(compressed);
        }

        output.Seek(offsetTablePos, SeekOrigin.Begin);
        for (var i = 0; i < chunkCount; i++)
            bw.Write(offsets[i]);
    }

    private static unsafe void ExtractBgr(IntPtr src, int rowPitch, int width, int yStart, int lineCount, byte[] dest)
    {
        var bytesPerChannel = width * HalfSize;
        var destLineSize = bytesPerChannel * ChannelCount;
        var basePtr = (byte*)src;

        for (var line = 0; line < lineCount; line++)
        {
            var row = basePtr + (long)(yStart + line) * rowPitch;
            var destLineOffset = line * destLineSize;

            var bOff = destLineOffset;
            var gOff = destLineOffset + bytesPerChannel;
            var rOff = destLineOffset + bytesPerChannel * 2;

            for (var x = 0; x < width; x++)
            {
                var px = row + x * SrcPixelStride;
                // RGBA16F layout: R(2) G(2) B(2) A(2)
                dest[bOff]     = px[4];
                dest[bOff + 1] = px[5];
                dest[gOff]     = px[2];
                dest[gOff + 1] = px[3];
                dest[rOff]     = px[0];
                dest[rOff + 1] = px[1];
                bOff += HalfSize;
                gOff += HalfSize;
                rOff += HalfSize;
            }
        }
    }

    private static byte[] ZlibCompress(byte[] raw)
    {
        var n = raw.Length;
        var tmp = new byte[n];

        var half = (n + 1) / 2;
        int ei = 0, oi = 0;
        for (var i = 0; i < n; i++)
        {
            if ((i & 1) == 0)
                tmp[ei++] = raw[i];
            else
                tmp[half + oi++] = raw[i];
        }

        var prev = (int)tmp[0];
        for (var i = 1; i < n; i++)
        {
            var cur = (int)tmp[i];
            tmp[i] = (byte)((cur - prev + 384) & 0xFF);
            prev = cur;
        }

        // zlib = header(2) + deflate + adler32(4)
        using var ms = new MemoryStream();
        ms.WriteByte(0x78);
        ms.WriteByte(0x9C);

        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            ds.Write(tmp, 0, n);

        var adler = Adler32(tmp, n);
        ms.WriteByte((byte)(adler >> 24));
        ms.WriteByte((byte)(adler >> 16));
        ms.WriteByte((byte)(adler >> 8));
        ms.WriteByte((byte)adler);

        return ms.ToArray();
    }

    // Standard zlib NMAX optimization: defer the modulo to every 5552 bytes instead
    // of every byte. NMAX = 5552 is the largest block where a and b both stay within
    // uint32 range before reduction - the bound comes from b's worst-case growth
    // sum(a_max + 255*k) for k=1..NMAX with a_max = MOD - 1 + NMAX*255, which has
    // to fit in 32 bits. A 4K HDR chunk (~720KB) shrinks from ~720k modulo ops to
    // ~260, saving tens of ms over the whole screenshot.
    private static uint Adler32(byte[] data, int length)
    {
        const uint mod = 65521;
        const int nmax = 5552;
        uint a = 1, b = 0;
        var i = 0;
        while (i < length)
        {
            var end = i + Math.Min(nmax, length - i);
            while (i < end)
            {
                a += data[i++];
                b += a;
            }
            a %= mod;
            b %= mod;
        }
        return (b << 16) | a;
    }

    private static void WriteHeader(BinaryWriter bw, int width, int height)
    {
        // Magic number
        bw.Write(20000630);
        // Version 2, single-part scanline
        bw.Write(2);

        // channels
        WriteNullTerminated(bw, "channels");
        WriteNullTerminated(bw, "chlist");
        var chlistSize = 0;
        foreach (var name in new[] { "B", "G", "R" })
            chlistSize += name.Length + 1 + 4 + 4 + 4 + 4; // name\0 + pixelType + pLinear(unused int) + xSampling + ySampling
        chlistSize += 1; // null terminator
        bw.Write(chlistSize);
        foreach (var name in new[] { "B", "G", "R" })
        {
            WriteNullTerminated(bw, name);
            bw.Write(1); // HALF
            bw.Write(0); // pLinear (unused, must be 0)
            bw.Write(1); // xSampling
            bw.Write(1); // ySampling
        }
        bw.Write((byte)0); // channel list terminator

        // compression
        WriteNullTerminated(bw, "compression");
        WriteNullTerminated(bw, "compression");
        bw.Write(1); // size
        bw.Write((byte)3); // ZIP

        // dataWindow
        WriteNullTerminated(bw, "dataWindow");
        WriteNullTerminated(bw, "box2i");
        bw.Write(16);
        bw.Write(0);
        bw.Write(0);
        bw.Write(width - 1);
        bw.Write(height - 1);

        // displayWindow
        WriteNullTerminated(bw, "displayWindow");
        WriteNullTerminated(bw, "box2i");
        bw.Write(16);
        bw.Write(0);
        bw.Write(0);
        bw.Write(width - 1);
        bw.Write(height - 1);

        // lineOrder
        WriteNullTerminated(bw, "lineOrder");
        WriteNullTerminated(bw, "lineOrder");
        bw.Write(1); // size
        bw.Write((byte)0); // INCREASING_Y

        // pixelAspectRatio
        WriteNullTerminated(bw, "pixelAspectRatio");
        WriteNullTerminated(bw, "float");
        bw.Write(4);
        bw.Write(1.0f);

        // screenWindowCenter
        WriteNullTerminated(bw, "screenWindowCenter");
        WriteNullTerminated(bw, "v2f");
        bw.Write(8);
        bw.Write(0.0f);
        bw.Write(0.0f);

        // screenWindowWidth
        WriteNullTerminated(bw, "screenWindowWidth");
        WriteNullTerminated(bw, "float");
        bw.Write(4);
        bw.Write(1.0f);

        // end of header
        bw.Write((byte)0);
    }

    private static void WriteNullTerminated(BinaryWriter bw, string s)
    {
        var bytes = Encoding.ASCII.GetBytes(s);
        bw.Write(bytes);
        bw.Write((byte)0);
    }
}
