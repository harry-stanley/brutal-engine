struct FrameData_std140_0
{
    @align(16) time_0 : f32,
    @align(4) pixelSnap_0 : f32,
    @align(8) viewportSize_0 : vec2<f32>,
};

@binding(0) @group(0) var<uniform> frame_0 : FrameData_std140_0;
struct _MatrixStorage_float4x4_ColMajorstd430_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct PushData_std430_0
{
    @align(16) viewProj_0 : _MatrixStorage_float4x4_ColMajorstd430_0,
    @align(16) tint_0 : vec4<f32>,
};

var<uniform> pc_0 : PushData_std430_0;
struct VSOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) uv_0 : vec2<f32>,
    @location(1) color_0 : vec4<f32>,
};

struct vertexInput_0
{
    @location(0) position_1 : vec2<f32>,
    @location(1) uv_1 : vec2<f32>,
    @location(2) color_1 : vec4<f32>,
};

@vertex
fn vertexMain( _S1 : vertexInput_0) -> VSOutput_0
{
    var o_0 : VSOutput_0;
    var p_0 : vec2<f32>;
    if((frame_0.pixelSnap_0) > 0.5f)
    {
        p_0 = floor(_S1.position_1 + vec2<f32>(0.5f));
    }
    else
    {
        p_0 = _S1.position_1;
    }
    o_0.position_0 = (((vec4<f32>(p_0, 0.0f, 1.0f)) * (mat4x4<f32>(pc_0.viewProj_0.data_0[i32(0)][i32(0)], pc_0.viewProj_0.data_0[i32(1)][i32(0)], pc_0.viewProj_0.data_0[i32(2)][i32(0)], pc_0.viewProj_0.data_0[i32(3)][i32(0)], pc_0.viewProj_0.data_0[i32(0)][i32(1)], pc_0.viewProj_0.data_0[i32(1)][i32(1)], pc_0.viewProj_0.data_0[i32(2)][i32(1)], pc_0.viewProj_0.data_0[i32(3)][i32(1)], pc_0.viewProj_0.data_0[i32(0)][i32(2)], pc_0.viewProj_0.data_0[i32(1)][i32(2)], pc_0.viewProj_0.data_0[i32(2)][i32(2)], pc_0.viewProj_0.data_0[i32(3)][i32(2)], pc_0.viewProj_0.data_0[i32(0)][i32(3)], pc_0.viewProj_0.data_0[i32(1)][i32(3)], pc_0.viewProj_0.data_0[i32(2)][i32(3)], pc_0.viewProj_0.data_0[i32(3)][i32(3)]))));
    o_0.uv_0 = _S1.uv_1;
    o_0.color_0 = _S1.color_1 * pc_0.tint_0;
    return o_0;
}

