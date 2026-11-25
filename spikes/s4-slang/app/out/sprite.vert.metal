#include <metal_stdlib>
#include <metal_math>
#include <metal_texture>
using namespace metal;

#line 90 "core"
struct vertexMain_Result_0
{
    float4 position_0 [[position]];
    float2 uv_0 [[user(TEXCOORD)]];
    float4 color_0 [[user(COLOR)]];
};


#line 90
struct vertexInput_0
{
    float2 position_1 [[attribute(0)]];
    float2 uv_1 [[attribute(1)]];
    float4 color_1 [[attribute(2)]];
};


#line 22 "sprite.slang"
struct FrameData_0
{
    float time_0;
    float pixelSnap_0;
    float2 viewportSize_0;
};


#line 1059 "core"
struct _MatrixStorage_float4x4_ColMajornatural_0
{
    array<float4, int(4)> data_0;
};


#line 1059
struct PushData_natural_0
{
    _MatrixStorage_float4x4_ColMajornatural_0 viewProj_0;
    float4 tint_0;
};


#line 5298 "core.meta.slang"
struct KernelContext_0
{
    FrameData_0 constant* frame_0;
    PushData_natural_0 constant* pc_0;
};


#line 9 "sprite.slang"
struct VSOutput_0
{
    float4 position_2;
    float2 uv_2;
    float4 color_2;
};


#line 9
[[vertex]] vertexMain_Result_0 vertexMain(vertexInput_0 _S1 [[stage_in]], FrameData_0 constant* frame_1 [[buffer(1)]], PushData_natural_0 constant* pc_1 [[buffer(0)]])
{

#line 9
    thread KernelContext_0 kernelContext_0;

#line 9
    (&kernelContext_0)->frame_0 = frame_1;

#line 9
    (&kernelContext_0)->pc_0 = pc_1;

#line 39
    thread VSOutput_0 o_0;

#line 39
    float2 p_0;

    if((frame_1->pixelSnap_0) > 0.5)
    {

#line 41
        p_0 = floor(_S1.position_1 + float2(0.5) );

#line 41
    }
    else
    {

#line 41
        p_0 = _S1.position_1;

#line 41
    }

    (&o_0)->position_2 = (((float4(p_0, 0.0, 1.0)) * (matrix<float,int(4),int(4)> ((&kernelContext_0)->pc_0->viewProj_0.data_0[int(0)][int(0)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(1)][int(0)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(2)][int(0)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(3)][int(0)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(0)][int(1)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(1)][int(1)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(2)][int(1)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(3)][int(1)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(0)][int(2)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(1)][int(2)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(2)][int(2)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(3)][int(2)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(0)][int(3)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(1)][int(3)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(2)][int(3)], (&kernelContext_0)->pc_0->viewProj_0.data_0[int(3)][int(3)]))));
    (&o_0)->uv_2 = _S1.uv_1;
    (&o_0)->color_2 = _S1.color_1 * (&kernelContext_0)->pc_0->tint_0;
    VSOutput_0 _S2 = o_0;

#line 46
    thread vertexMain_Result_0 _S3;

#line 46
    (&_S3)->position_0 = _S2.position_2;

#line 46
    (&_S3)->uv_0 = _S2.uv_2;

#line 46
    (&_S3)->color_0 = _S2.color_2;

#line 46
    return _S3;
}

