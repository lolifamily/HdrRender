// The engine's sprite pixel shader (Primitives/Sprites.hlsl) for the scRGB render
// target, see UiGammaPatch: the same premultiplied linear color, then as a gamma 2.2
// monitor shows it, like the scene (HdrTonemap.hlsl sdr_on_gamma22). The engine's
// sRGB backbuffer encoded the UI with the piecewise sRGB curve and the monitor
// decoded it with 2.2. It runs on the premultiplied color, as the backbuffer
// blended it: exact over black, where the dark tones this is about sit.
// UiBlendPatch then scales it to UI brightness in the blend unit.
//
// ps_sprites for textures that are premultiplied already (the engine's plain
// variant); ps_sprites_pm premultiplies them here (its PREMULTIPLY_ALPHA variant).
// Bindings as the engine's: t0 = sprite, t1 = alpha mask, s2 = its LinearSampler.

Texture2D SpriteTexture : register(t0);
Texture2D MaskTexture   : register(t1);
SamplerState LinearSampler : register(s2);

// The engine's sprite vertex shader output
struct ProcessedVertex
{
    float4 position  : SV_Position;
    float4 color     : COLOR;
    float2 texcoord0 : TEXCOORD0;
};

// Engine Math/Color.hlsli
float3 rgb_to_srgb(float3 rgb)
{
    return (rgb <= 0.0031308) ? rgb * 12.92 : (pow(abs(rgb), 1 / 2.4) * 1.055 - 0.055);
}

float4 draw_sprite(ProcessedVertex input, bool premultiply)
{
    float4 sample = SpriteTexture.Sample(LinearSampler, input.texcoord0);
    float mask = MaskTexture.Sample(LinearSampler, input.texcoord0).r;
    if (premultiply)
        sample.rgb *= sample.a;
    float4 color = sample * input.color * mask;
    return float4(pow(max(rgb_to_srgb(color.rgb), 0.0), 2.2), color.a);
}

float4 ps_sprites(ProcessedVertex input) : SV_Target0
{
    return draw_sprite(input, false);
}

float4 ps_sprites_pm(ProcessedVertex input) : SV_Target0
{
    return draw_sprite(input, true);
}
