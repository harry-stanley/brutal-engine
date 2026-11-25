// Realistic 2D sprite shader for the S4 spike.
struct VSInput
{
    float2 position : POSITION;
    float2 uv       : TEXCOORD0;
    float4 color    : COLOR0;
};

struct VSOutput
{
    float4 position : SV_Position;
    float2 uv       : TEXCOORD0;
    float4 color    : COLOR0;
};

struct PushData
{
    float4x4 viewProj;
    float4   tint;
};

struct FrameData
{
    float  time;
    float  pixelSnap;
    float2 viewportSize;
};

[[vk::push_constant]] ConstantBuffer<PushData> pc;

[[vk::binding(0, 0)]] ConstantBuffer<FrameData> frame;   // uniform buffer
[[vk::binding(1, 0)]] Sampler2D spriteTexture;           // combined image sampler
[[vk::binding(0, 1)]] Texture2D maskTexture;             // separate texture
[[vk::binding(1, 1)]] SamplerState maskSampler;          // separate sampler

[shader("vertex")]
VSOutput vertexMain(VSInput input)
{
    VSOutput o;
    float2 p = input.position;
    if (frame.pixelSnap > 0.5)
        p = floor(p + 0.5);
    o.position = mul(pc.viewProj, float4(p, 0.0, 1.0));
    o.uv = input.uv;
    o.color = input.color * pc.tint;
    return o;
}

[shader("fragment")]
float4 fragmentMain(VSOutput input) : SV_Target
{
    float4 c = spriteTexture.Sample(input.uv);
    float mask = maskTexture.Sample(maskSampler, input.uv).r;
    return c * input.color * mask;
}
