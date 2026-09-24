// Graphics-normalized (1.0 = UI brightness) -> scRGB (1.0 = 80 nits).
// Both things that reach the render target go through here: the copy that ends
// the post-process chain (ChainCopyPatch) and the UI composite
// (ConsumeMainSpritesPatch). Drawn with MyScreenPass.DrawFullscreenQuad, so the
// input is the engine's PostprocessCopy vertex output.
//
// rgb is scaled, alpha passes through. The UI layer is premultiplied: Sprites.hlsl
// multiplies the vertex color by alpha, and the texture sample too under
// PREMULTIPLY_ALPHA, and MySpritesManager blends with BlendAlphaPremult. The
// composite uses the same blend state, so rgb scales linearly and must not be
// multiplied by alpha again - that would square the alpha and crush every
// translucent UI element (tooltips, HUD fades, modal dimmers).

Texture2D source : register(t0);
SamplerState linear_sampler : register(s2);   // engine LinearSampler slot

cbuffer OutputConstants : register(b0)
{
    float graphics_white;   // scRGB
};

struct ps_input
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

float4 encode(float4 c)
{
    return float4(c.rgb * graphics_white, c.a);
}

// Same size, texel for texel (engine PostprocessCopy)
float4 ps_copy(ps_input input) : SV_Target
{
    return encode(source[uint2(input.pos.xy)]);
}

// Size mismatch or DRS stretch (engine PostprocessCopyFilter / PostprocessStretch)
float4 ps_filter(ps_input input) : SV_Target
{
    return encode(source.SampleLevel(linear_sampler, input.uv, 0));
}
