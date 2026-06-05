Texture2D ui_layer : register(t0);
SamplerState point_sampler : register(s0);

cbuffer Constants : register(b0)
{
    float paper_white;
};

struct vs_output
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

vs_output vs_main(uint id : SV_VertexID)
{
    vs_output o;
    o.uv = float2((id << 1) & 2, id & 2);
    o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
    return o;
}

float4 ps_main(vs_output input) : SV_Target
{
    float4 ui = ui_layer.Sample(point_sampler, input.uv);

    // SE's sprite pipeline writes premultiplied alpha into the offscreen RT:
    // Sprites.hlsl vs pre-multiplies vertex color (output.color.rgb *= a),
    // ps pre-multiplies sample under PREMULTIPLY_ALPHA, and SpritesManager.Render
    // selects the variant so the final SV_Target0 is premul either way.
    // ConsumeMainSpritesPatch composites with BlendAlphaPremult (Src=One,
    // Dst=InverseSourceAlpha), which already expects a premultiplied src.
    // Multiplying rgb by ui.a again here would square the alpha and crush
    // every translucent UI element (tooltips, HUD fades, modal dimmers).
    return float4(ui.rgb * paper_white, ui.a);
}
