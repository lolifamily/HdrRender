Texture2D source_tex : register(t0);
Texture2D<float2> avg_luminance : register(t1);
Texture2D bloom_tex : register(t2);
Texture2D<float> dirt_tex : register(t3);

RWTexture2D<float4> destination : register(u0);

SamplerState bilinear_sampler : register(s0);

// Three units meet in this shader:
//   scene-normalized     1.0 = paper white, the scene's SDR reference white
//   scRGB                1.0 = 80 nits; the *_white and *peak constants are scRGB
//   graphics-normalized  1.0 = graphics white = UI brightness
// The output is graphics-normalized. What the engine draws into the chain after
// this pass (highlight outlines, LDR / PostPP billboards) is SDR content authored
// for 1.0 = SDR white, like the UI, so it lands at UI brightness with no per-pass
// scaling. OutputEncode.hlsl converts to scRGB once, where the chain is copied
// into the render target.
cbuffer HdrConstants : register(b0)
{
    float paper_white;
    float graphics_white;
    float peak;
    float source_peak;          // >= peak, see TonemapPatch

    float sdr_gain;             // see TonemapPatch.SdrGainAt
    float bloom_mult;
    float bloom_dirt_ratio;
    float black_lift;

    float grain_strength;
    float grain_amount;
    int   grain_size;
    float frame_time;

    float contrast;
    float brightness;
    float saturation;
    float vibrance;

    float brightness_r;
    float brightness_g;
    float brightness_b;
    float sepia_strength;

    float3 light_color;
    int    disable_tonemapping;

    float3 dark_color;
    int    needs_alpha_luminance;

    float  white_point;         // the engine's Hable white point
    float  natural_color;       // see map_to_display
    float2 padding;
};

// ---------------------------------------------------------------------------
// Park-Miller LCG (ported from engine Random.hlsli)
// ---------------------------------------------------------------------------
#define RNG_IA 16807
#define RNG_IM 2147483647
#define RNG_AM (1.0f / float(RNG_IM))
#define RNG_IQ 127773u
#define RNG_IR 2836
#define RNG_MASK 123459876

struct random_generator
{
    int seed;

    void set_seed(uint value)
    {
        seed = int(value);
        cycle();
    }

    float get_float()
    {
        cycle();
        return RNG_AM * seed;
    }

    void cycle()
    {
        seed ^= RNG_MASK;
        int k = seed / RNG_IQ;
        seed = RNG_IA * (seed - k * RNG_IQ) - RNG_IR * k;
        if (seed < 0)
            seed += RNG_IM;
        seed ^= RNG_MASK;
    }
};

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
float get_relative_luminance(float3 rgb)
{
    return dot(rgb, float3(0.2126, 0.7152, 0.0722));
}

float to_grayscale(float3 color)
{
    return dot(float3(0.3, 0.59, 0.11), color);
}

float max3(float3 c)
{
    return max(max(c.r, c.g), c.b);
}

static const float flt_max = 3.402823466e+38;

// Engine Math/Color.hlsli
float3 rgb_to_srgb(float3 rgb)
{
    return (rgb <= 0.0031308) ? rgb * 12.92 : (pow(abs(rgb), 1 / 2.4) * 1.055 - 0.055);
}

// ---------------------------------------------------------------------------
// SMPTE ST 2084 (PQ) encode/decode.
// scRGB 1.0 = 80 nits; PQ 1.0 = 10000 nits; so 125 scRGB = PQ 1.0.
// ---------------------------------------------------------------------------
static const float pq_m1 = 0.1593017578125;   // 2610/16384
static const float pq_m2 = 78.84375;          // (2523/4096) * 128
static const float pq_c1 = 0.8359375;         // 3424/4096
static const float pq_c2 = 18.8515625;        // (2413/4096) * 32
static const float pq_c3 = 18.6875;           // (2392/4096) * 32

float linear_to_pq(float l)
{
    float y    = max(l / 125.0, 0.0);
    float y_m1 = pow(y, pq_m1);
    return pow((pq_c1 + pq_c2 * y_m1) / (1.0 + pq_c3 * y_m1), pq_m2);
}

float pq_to_linear(float pq)
{
    float v_m2 = pow(max(pq, 0.0), 1.0 / pq_m2);
    float num  = max(v_m2 - pq_c1, 0.0);
    float den  = max(pq_c2 - pq_c3 * v_m2, 1e-6);
    float y    = pow(num / den, 1.0 / pq_m1);
    return y * 125.0;
}

// ---------------------------------------------------------------------------
// Film grain (ported from engine Main.hlsl, applied before tonemap)
// ---------------------------------------------------------------------------
float3 apply_grain(float3 source, uint2 texel)
{
    if (grain_strength <= 0)
        return source;

    random_generator rng;
    float rounding = 1;

    if (grain_size > 0)
    {
        int gs = grain_size * 2 + 1;
        float2 dist = (float2)(texel % gs) - grain_size;
        rounding = 1 - dot(dist, dist) / (grain_size * grain_size * 2.0f);
        rng.set_seed(((texel.x + gs) / gs) * ((texel.y + gs) / gs) * int(frame_time * 1000));
    }
    else
    {
        rng.set_seed(texel.x * texel.y * int(frame_time * 1000));
    }

    source -= saturate(grain_amount - rng.get_float()) * rounding * grain_strength;
    return source;
}

// ---------------------------------------------------------------------------
// Color filters (ported from engine Filters.hlsli), on scene-normalized color:
// the SDR range they were designed for.
// ---------------------------------------------------------------------------
float3 apply_basic_filters(float3 color)
{
    float br = brightness * brightness_r;
    float bg = brightness * brightness_g;
    float bb = brightness * brightness_b;
    float4x4 brightness_mat = float4x4(
        br, 0, 0, 0,
         0,bg, 0, 0,
         0, 0,bb, 0,
         0, 0, 0, 1);

    float4x4 contrast_mat = float4x4(
        contrast, 0, 0, 0,
        0, contrast, 0, 0,
        0, 0, contrast, 0,
        -0.5 * contrast + 0.5, -0.5 * contrast + 0.5, -0.5 * contrast + 0.5, 1);

    const float rw = 0.3086;
    const float gw = 0.6094;
    const float bw = 0.0820;
    float s = saturation;
    float4x4 sat_mat = float4x4(
        (1 - s)*rw + s, (1 - s)*rw,     (1 - s)*rw,     0,
        (1 - s)*gw,     (1 - s)*gw + s, (1 - s)*gw,     0,
        (1 - s)*bw,     (1 - s)*bw,     (1 - s)*bw + s, 0,
        0,              0,              0,              1);

    float4x4 m = mul(mul(brightness_mat, contrast_mat), sat_mat);
    return mul(float4(color, 1), m).rgb;
}

float3 apply_vibrance(float3 rgb, float sat_limit)
{
    float lum = get_relative_luminance(rgb);
    float minc = min(min(rgb.r, rgb.g), rgb.b);
    float maxc = max(max(rgb.r, rgb.g), rgb.b);
    // The engine runs this on SDR output, where max - min stays below ~1.3. Above
    // paper white the raw difference grows with brightness: the stock vibrance (0.2)
    // would desaturate HDR highlights and larger values invert them. HDR values
    // measure it on the SDR range (sat_limit 1), which keeps the engine's result
    // there and leaves highlights alone; the engine's own color measures it unclamped.
    float sat = min(maxc - minc, sat_limit);
    float s = 1.0 + (vibrance * (1.0 - (sign(vibrance) * sat)));
    return lerp(lum, rgb, s);
}

float3 apply_sepia(float3 color)
{
    float gray = saturate(to_grayscale(color));
    float3 sepia = lerp(dark_color, light_color, gray);
    return lerp(color, sepia, sepia_strength);
}

// The engine's filter chain, in its order (Postprocess/Tonemapping/Main.hlsl).
float3 apply_filters(float3 color, float sat_limit)
{
    return apply_sepia(apply_vibrance(apply_basic_filters(color), sat_limit));
}

// ---------------------------------------------------------------------------
// The vanilla SDR color of the pixel, as the engine's tonemap computes it
// (Postprocess/Tonemapping/Main.hlsl): its filmic curve per channel over the
// curve's value at the white point, the filters, saturate. Its channels fade to
// white as they approach the white point, the look the content was authored for.
// ---------------------------------------------------------------------------
float3 hable(float3 x)
{
    const float A = 0.15, B = 0.50, C = 0.10, D = 0.20, E = 0.02, F = 0.30;
    return (x * (A * x + C * B) + D * E) / (x * (A * x + B) + D * F) - E / F;
}

float3 vanilla_color(float3 color)
{
    float3 curve = hable(color) / hable(max(white_point, 1e-3).xxx);
    return saturate(apply_filters(curve, flt_max));
}

// ---------------------------------------------------------------------------
// BT.2390 EETF, scRGB in and out. KS is derived from the display / source peak
// ratio; below it the curve is identity, above it a Hermite shoulder (C1 at KS,
// flat at the end) lands exactly on peak at source_peak. The black level lift
// comes after the shoulder, as E3 in BT.2390.
// ---------------------------------------------------------------------------
float eetf(float l)
{
    float source_pq = max(linear_to_pq(source_peak), 1e-6);
    float max_lum   = saturate(linear_to_pq(peak) / source_pq);
    float ks        = saturate(1.5 * max_lum - 0.5);

    float e = saturate(linear_to_pq(l) / source_pq);
    if (e > ks)
    {
        float t  = (e - ks) / max(1.0 - ks, 1e-6);
        float t2 = t * t;
        float t3 = t2 * t;
        e =  (2.0 * t3 - 3.0 * t2 + 1.0) * ks
          +  (t3 - 2.0 * t2 + t) * (1.0 - ks)
          +  (-2.0 * t3 + 3.0 * t2) * max_lum;
    }

    float one_minus_e    = 1.0 - e;
    float one_minus_e_sq = one_minus_e * one_minus_e;
    e += black_lift * one_minus_e_sq * one_minus_e_sq;

    return pq_to_linear(e * source_pq);
}

// ---------------------------------------------------------------------------
// Display mapping: one EETF, on max(R,G,B), so no channel can exceed peak and
// no second clip compresses near-neutral highlights short of it. The color
// direction, max(R,G,B) = 1, blends by natural_color from the vanilla color's
// (0: the authored look, bright colors fade to white) to the HDR color's own
// (1: hue and saturation kept at any brightness, as the eye sees it). n is
// scene-normalized, in and out; vanilla is vanilla_color() of the same pixel.
// ---------------------------------------------------------------------------
float3 map_to_display(float3 n, float3 vanilla)
{
    float m = max3(n);
    float3 vanilla_dir = vanilla / max(max3(vanilla), 1e-6);
    float3 hdr_dir = n / max(m, 1e-6);
    return eetf(m * paper_white) / paper_white * lerp(vanilla_dir, hdr_dir, natural_color);
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------
[numthreads(8, 8, 1)]
void cs_main(uint3 dtid : SV_DispatchThreadID)
{
    uint2 texel = dtid.xy;

    uint w, h;
    destination.GetDimensions(w, h);
    float2 uv = (texel + 0.5) / float2(w, h);

    // 1. Film grain on the raw LBuffer, before exposure (engine order).
    float3 source = apply_grain(source_tex[texel].xyz, texel);

    float3 n;
    if (disable_tonemapping)
    {
        // The engine's DISABLE_TONEMAPPING variant (debug overrides only): the raw
        // LBuffer, no exposure, bloom or curve, through the filters, clipped at white.
        n = saturate(apply_filters(source, flt_max));
    }
    else
    {
        // 2. Scene-normalized color. The engine exposes the average scene to ~1,
        //    adds bloom and maps through Hable, whose slope at black is sdr_gain of
        //    SDR white; scaling by it keeps the engine's shadows and midtones and
        //    anchors them to paper white.
        float dirt = dirt_tex.SampleLevel(bilinear_sampler, uv, 0) * bloom_dirt_ratio + (1 - bloom_dirt_ratio);
        float3 bloom = bloom_tex.SampleLevel(bilinear_sampler, uv, 0).xyz * bloom_mult * dirt;
        float3 color = exp2(avg_luminance[uint2(0, 0)].g) * source + bloom;

        // 3. Color filters, before the display mapping: whatever they produce still
        //    goes through it, so nothing overshoots peak.
        n = max(apply_filters(sdr_gain * color, 1.0), 0);

        // 4. Display mapping. A NaN or +Inf in the scene (both LBuffer formats keep
        //    them) turns the mapping into NaN; the engine's saturate() makes such a
        //    pixel black, max() does the same here (it returns the non-NaN operand).
        n = max(map_to_display(n, vanilla_color(color)), 0);
    }

    // 5. Alpha - FXAA reads perceptual luma from .w when needs_alpha_luminance is
    //    set: the engine's is the luma of its sRGB-encoded SDR image, which this
    //    matches exactly up to paper white. Otherwise downstream alpha-blend
    //    (highlight, billboards) expects 1.0.
    float alpha = needs_alpha_luminance ? get_relative_luminance(rgb_to_srgb(saturate(n))) : 1.0;

    destination[texel] = float4(n * (paper_white / graphics_white), alpha);
}
