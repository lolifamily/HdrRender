Texture2D source_tex : register(t0);
Texture2D<float2> avg_luminance : register(t1);
Texture2D bloom_tex : register(t2);
Texture2D<float> dirt_tex : register(t3);

RWTexture2D<float4> destination : register(u0);

SamplerState bilinear_sampler : register(s0);

cbuffer HdrConstants : register(b0)
{
    float paper_white;
    float peak;
    float source_peak;
    float bloom_mult;

    float bloom_dirt_ratio;
    float grain_strength;
    float grain_amount;
    int   grain_size;

    float contrast;
    float brightness;
    float saturation;
    float brightness_r;

    float brightness_g;
    float brightness_b;
    float vibrance;
    float sepia_strength;

    float3 light_color;
    float frame_time;

    float3 dark_color;
    float black_lift;

    int disable_postprocess;
    int needs_alpha_luminance;
    int pad0;
    int pad1;
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
// Color filters (ported from engine Filters.hlsli, in paper-white-normalized space)
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

float3 apply_vibrance(float3 rgb)
{
    float lum = get_relative_luminance(rgb);
    float minc = min(min(rgb.r, rgb.g), rgb.b);
    float maxc = max(max(rgb.r, rgb.g), rgb.b);
    float sat = maxc - minc;
    float s = 1.0 + (vibrance * (1.0 - (sign(vibrance) * sat)));
    return lerp(lum, rgb, s);
}

float3 apply_sepia(float3 color)
{
    float gray = saturate(to_grayscale(color));
    float3 sepia = lerp(dark_color, light_color, gray);
    return lerp(color, sepia, sepia_strength);
}

// ---------------------------------------------------------------------------
// Hue-preserving channel soft clip: when any channel exceeds peak*0.8, scale
// the whole rgb triplet so max_c rolls off toward peak. Keeps R/G/B ratios
// (hue) intact and only compresses brightness. Prevents a single overbright
// channel from being hard-clipped by the display while everything else stays
// linear.
//
// Earlier versions also lerped the result toward grayscale ("path to white"),
// modeling the SDR-era assumption that very bright pixels read as white. With
// the scene now anchored to paper_white nits, mid-bright emissive (LBuffer
// 5-15, ~1000-3000 nits) routinely crosses peak*0.8, and the desat lerp made
// saturated emissive colors (battery LEDs, neon panels, engine plumes) look
// washed-out. HDR displays exist precisely so saturated highlights can stay
// saturated, so the desat step is gone.
// ---------------------------------------------------------------------------
float3 apply_channel_soft_clip(float3 color)
{
    float max_c = max(max(color.r, color.g), color.b);
    float start = peak * 0.8;

    if (max_c <= start)
        return color;

    float x = max_c - start;
    float d = peak - start;

    float compressed = peak - d * d / (x + d);
    return color * (compressed / max_c);
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

    float3 source = source_tex[texel].xyz;

    // 1. Film grain (before tonemap, matches engine order)
    source = apply_grain(source, texel);

    // 2. Exposure: SE eye-adaptation only.
    // (SE WhitePoint is a Hable-filmic parameter, irrelevant to our EETF.)
    float exposure = exp2(avg_luminance[uint2(0, 0)].g);
    float3 color = exposure * source;

    // 3. Bloom - matches SE behavior: PP off skips bloom but keeps grain and color filters
    if (!disable_postprocess)
    {
        float dirt = dirt_tex.SampleLevel(bilinear_sampler, uv, 0) * bloom_dirt_ratio + (1 - bloom_dirt_ratio);
        color += bloom_tex.SampleLevel(bilinear_sampler, uv, 0).xyz * bloom_mult * dirt;
    }

    // Note: SE's LBuffer is SDR-tonemap-ready (1.0 = SDR display white), not
    // a paper-white-anchored absolute-nits buffer like modern HDR engines.
    // Inverse-tone-mapping the LBuffer by multiplying paper_white here would
    // over-brighten mid-bright illumination (firelight on surrounding
    // surfaces, ambient glow) by ~2.5x while leaving the actual emissive
    // cores unchanged (EETF clips them to display peak either way). The
    // result destroys SE's intended SDR contrast hierarchy ("cozy campfire"
    // turns into "scene-wide overexposure"). We treat LBuffer values as
    // direct scRGB instead - LBuffer 1.0 = scRGB 1.0 = 80 nits SDR
    // reference white. paper_white still anchors the UI composite path.

    // 4. BT.2390 EETF in PQ space - standard display mapping.
    // Forward parameterization: source_peak (config) is the expected scene
    // max luminance; KS auto-derived via BT.2390 standard formula:
    //   max_lum_norm = display_peak_pq / source_peak_pq
    //   KS = 1.5 * max_lum_norm - 0.5
    // When source <= display the curve collapses to linear hard-cap.
    float lum = max(get_relative_luminance(color), 1e-6);

    float display_peak_pq = linear_to_pq(peak);
    float source_peak_pq  = max(linear_to_pq(source_peak), 1e-6);
    float max_lum_norm    = saturate(display_peak_pq / source_peak_pq);
    float ks              = clamp(1.5 * max_lum_norm - 0.5, 0.0, 1.0);

    float lum_pq = linear_to_pq(lum);
    float e      = saturate(lum_pq / source_peak_pq);

    // Black lift in PQ space (BT.2390 Annex 3, perceptually uniform now).
    float one_minus_e    = 1.0 - e;
    float one_minus_e_sq = one_minus_e * one_minus_e;
    e = e + black_lift * one_minus_e_sq * one_minus_e_sq;

    // Cubic Hermite shoulder in PQ space (C1 continuous at KS, tangent 0 at peak).
    float e_out;
    if (e < ks)
    {
        e_out = e;
    }
    else
    {
        float t  = (e - ks) / max(1.0 - ks, 1e-6);
        float t2 = t * t;
        float t3 = t2 * t;
        e_out =  (2.0 * t3 - 3.0 * t2 + 1.0) * ks
              +  (t3 - 2.0 * t2 + t) * (1.0 - ks)
              +  (-2.0 * t3 + 3.0 * t2) * max_lum_norm;
    }

    float mapped_lum_pq = e_out * source_peak_pq;
    float mapped_lum    = pq_to_linear(mapped_lum_pq);
    float3 hdr          = color * (mapped_lum / lum);

    // 5. Color filters (in paper-white-normalized space)
    float3 normalized = hdr / paper_white;
    normalized = apply_basic_filters(normalized);
    normalized = apply_vibrance(normalized);
    normalized = apply_sepia(normalized);
    hdr = max(normalized * paper_white, 0);

    // 6. Channel soft clip (hue-preserving, no desat)
    hdr = apply_channel_soft_clip(hdr);

    // 7. Alpha - FXAA reads .w as luma when needs_alpha_luminance is set;
    // otherwise downstream alpha-blend (highlight, billboards) expects 1.0.
    float out_lum = get_relative_luminance(hdr);
    float alpha = needs_alpha_luminance ? (out_lum / (1.0 + out_lum)) : 1.0;

    destination[texel] = float4(hdr, alpha);
}
