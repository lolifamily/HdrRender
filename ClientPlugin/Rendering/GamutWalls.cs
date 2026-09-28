using System;
using VRage.Render11.Common;
using VRage.Render11.Resources;

namespace ClientPlugin.Rendering;

// The table behind HdrTonemap.hlsl expand_gamut: one float4 per Oklab hue, the
// first three as Oklab saturation S = C / L.
//   x, y  the gamut walls, BT.709 and Display P3: the largest S before a
//         channel goes below 0. Brightness is left to the tonemap, so that is
//         the only wall, and scaling a color does not move it: Oklab is
//         homogeneous, one S per hue holds at every lightness.
//   z     the edge of the natural tones, below which nothing moves; the
//         shader draws it toward grey by natural_tones.
//   w     unused, keeps the 16-byte stride.
//
// A wall is the first exit on the way out from grey, not a bisection over a
// wide range: near the blue primary Oklab's cube root bends the gamut edge
// across the hue line, which leaves the gamut and comes back in at the primary.
// Each bin keeps the least first exit of its neighborhood, so that
// interpolating between bins stays inside too.
//
// The natural tones are the "sacred region" of Ward, Yoo, Soudi and Akhavan,
// Exploiting Wide-gamut Displays (IS&T CIC 2016): a disc in CIE 1976 u'v'
// around earth and flesh tones, from measurements of natural tones, taken with
// their RGB-to-XYZ matrix, which leaves out the D65 white point conversion
// because the viewer is adapted to display white. Its edge lies close to grey
// toward blue and violet, where a more saturated sky is the preferred
// reproduction, and far out toward yellow and yellow-green, where skin and
// grass are best kept as they are (Hunt, Pitt and Winter 1974).
//
// Read per pixel at the pixel's own hue, so a structured buffer rather than
// constants: constant reads that differ across a warp are serialized on NVIDIA.
// Created once with its data (HdrResources.InitShaders) and never written after.
// The table, and the shader code that reads it, are shared with HdrOutput2.
internal static class GamutWalls
{
    public const int Bins = 1024;        // over the hue circle from -pi
    private const int Fine = 4;          // samples per bin for the neighborhood minimum

    private static readonly double[,] Identity = { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
    private static readonly double[,] Bt709ToP3 =
    {
        { 0.8224621, 0.1775380, 0.0000000 },
        { 0.0331942, 0.9668058, 0.0000000 },
        { 0.0170826, 0.0723974, 0.9105199 }
    };

    // Centre and radius of the natural tones in u'v', as the paper gives them.
    private const double NaturalU = 0.217, NaturalV = 0.483, NaturalRadius = 0.051;

    public static unsafe ISrvBuffer CreateBuffer()
    {
        var bt709 = Walls(Identity);
        var p3 = Walls(Bt709ToP3);
        var table = new float[Bins * 4];
        for (var i = 0; i < Bins; i++)
        {
            table[4 * i] = bt709[i];
            table[4 * i + 1] = p3[i];
            table[4 * i + 2] = (float)FirstExit(-Math.PI + 2 * Math.PI * i / Bins, InNaturalTones);
        }

        fixed (float* data = table)
            return MyManagers.Buffers.CreateSrv("HdrOutput.GamutWalls", Bins, 4 * sizeof(float), new IntPtr(data));
    }

    private static float[] Walls(double[,] fromBt709)
    {
        Func<double, double, bool> inside = (a, b) => InGamut(a, b, fromBt709);
        var fine = new double[Bins * Fine];
        for (var i = 0; i < fine.Length; i++)
            fine[i] = FirstExit(-Math.PI + 2 * Math.PI * i / fine.Length, inside);

        var table = new float[Bins];
        for (var i = 0; i < Bins; i++)
        {
            var least = double.MaxValue;
            for (var j = -Fine; j <= Fine; j++)
                least = Math.Min(least, fine[(i * Fine + j + fine.Length) % fine.Length]);
            table[i] = (float)(least * 0.995); // margin for what the samples miss between them
        }
        return table;
    }

    // The largest S on the Oklab hue ray from grey before `inside` first turns false.
    private static double FirstExit(double hue, Func<double, double, bool> inside)
    {
        double a = Math.Cos(hue), b = Math.Sin(hue);
        const double step = 0.004, limit = 3.0;
        double last = 0, first = limit;
        for (var s = step; s < limit; s += step)
        {
            if (!inside(s * a, s * b))
            {
                first = s;
                break;
            }
            last = s;
        }

        for (var i = 0; i < 40; i++)
        {
            var mid = 0.5 * (last + first);
            if (inside(mid * a, mid * b))
                last = mid;
            else
                first = mid;
        }
        return last;
    }

    // Oklab (L = 1, a, b) in the gamut: no channel below 0.
    private static bool InGamut(double a, double b, double[,] fromBt709)
    {
        var (r, g, bl) = ToBt709(a, b);
        for (var k = 0; k < 3; k++)
            if (fromBt709[k, 0] * r + fromBt709[k, 1] * g + fromBt709[k, 2] * bl < 0)
                return false;
        return true;
    }

    // Oklab (L = 1, a, b) among the natural tones: the paper's matrix to XYZ,
    // then u'v'.
    private static bool InNaturalTones(double a, double b)
    {
        var (r, g, bl) = ToBt709(a, b);
        var x = 0.497 * r + 0.339 * g + 0.164 * bl;
        var y = 0.256 * r + 0.678 * g + 0.066 * bl;
        var z = 0.023 * r + 0.113 * g + 0.864 * bl;
        var d = x + 15 * y + 3 * z;
        double du = 4 * x / d - NaturalU, dv = 9 * y / d - NaturalV;
        return du * du + dv * dv < NaturalRadius * NaturalRadius;
    }

    // Oklab (L = 1, a, b) to linear BT.709 (Ottosson's inverse); HdrTonemap.hlsl
    // from_oklab is the same.
    private static (double R, double G, double B) ToBt709(double a, double b)
    {
        var l = 1 + 0.3963377774 * a + 0.2158037573 * b;
        var m = 1 - 0.1055613458 * a - 0.0638541728 * b;
        var s = 1 - 0.0894841775 * a - 1.2914855480 * b;
        l *= l * l;
        m *= m * m;
        s *= s * s;
        return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
                -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
                -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }
}
