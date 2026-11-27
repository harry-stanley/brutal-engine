// Test fixture for brutal-cook: one vertex and one fragment entry point, with a push-constant matrix.

struct PushData
{
    float4x4 transform;
    float4 tint;
};

[[vk::push_constant]]
ConstantBuffer<PushData> pc;

struct VertexOutput
{
    float4 position : SV_Position;
    float4 color : COLOR0;
};

[shader("vertex")]
VertexOutput vertMain(float2 position : POSITION, float4 color : COLOR0)
{
    VertexOutput output;
    output.position = mul(pc.transform, float4(position, 0.0, 1.0));
    output.color = color * pc.tint;
    return output;
}

[shader("fragment")]
float4 fragMain(VertexOutput input) : SV_Target
{
    return input.color;
}
