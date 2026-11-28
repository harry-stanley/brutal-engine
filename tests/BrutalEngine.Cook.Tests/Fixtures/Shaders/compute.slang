// Test fixture for brutal-cook: one compute entry point.

RWStructuredBuffer<float> values;

[shader("compute")]
[numthreads(64, 1, 1)]
void csMain(uint3 id : SV_DispatchThreadID)
{
    values[id.x] = values[id.x] * 2.0;
}
