// Port of the engine's Postprocess/ChromaticAberration/ChromaticAberration.hlsl.
// The engine declares its output RWTexture2D<unorm float4>. Bound to the FP16 UAV
// that CustomTextureFormatPatch gives the post-process chain, that is a UAV
// return-type mismatch, which D3D11 leaves undefined: a driver may clamp every
// value to [0, 1]. The pass runs every frame with stock settings (ChromaticFactor
// 0.02), so the same math runs here with a float UAV. The iteration count comes
// from the constant buffer instead of the engine's ITERATIONS macro.

Texture2D source : register(t0);
RWTexture2D<float4> destination : register(u0);
SamplerState bilinear_sampler : register(s0);

cbuffer ChromaticConstants : register(b0)
{
    float chromatic_factor;
    float vignette_start;
    float vignette_length;
    int   iterations;
};

float linterp(float t)
{
    return saturate(1.0 - abs(2.0 * t - 1.0));
}

float remap(float t, float a, float b)
{
    return saturate((t - a) / (b - a));
}

float4 spectrum_offset(float t)
{
    float lo = step(t, 0.5);
    float hi = 1.0 - lo;
    float w = linterp(remap(t, 1.0 / 6.0, 5.0 / 6.0));
    float4 ret = float4(lo, 1.0, hi, 1.0) * float4(1.0 - w, w, 1.0 - w, 1.0);
    return pow(abs(ret), 1.0 / 2.2);
}

[numthreads(8, 8, 1)]
void cs_main(uint3 dtid : SV_DispatchThreadID)
{
    uint w, h;
    destination.GetDimensions(w, h);
    float2 uv = (dtid.xy + 0.5) / float2(w, h);

    float2 cc = uv - 0.5;
    float dist = dot(cc, cc);
    cc *= dist;

    int num_iter = iterations * 3;
    float4 sum_col = 0;
    float4 sum_w = 0;
    [loop]
    for (int i = 0; i < num_iter; ++i)
    {
        float t = float(i) / float(num_iter);
        float4 weight = spectrum_offset(t);
        sum_w += weight;
        sum_col += weight * source.SampleLevel(bilinear_sampler, uv - cc * (chromatic_factor * t), 0);
    }

    float vignette = 1 - pow(abs(dist * vignette_start), vignette_length);
    destination[dtid.xy] = float4((sum_col / sum_w).rgb * vignette, 1);
}
