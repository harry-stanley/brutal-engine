// Test fixture for brutal-cook: the vertex entry point is missing a ';', so Slang must report a syntax error.

[shader("vertex")]
float4 vertMain(float2 position : POSITION) : SV_Position
{
    float4 result = float4(position, 0.0, 1.0)
    return result;
}

[shader("fragment")]
float4 fragMain() : SV_Target
{
    return float4(1.0, 1.0, 1.0, 1.0);
}
