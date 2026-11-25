#pragma pack_matrix(column_major)
#ifdef SLANG_HLSL_ENABLE_NVAPI
#include "nvHLSLExtns.h"
#endif

#ifndef __DXC_VERSION_MAJOR
// warning X3557: loop doesn't seem to do anything, forcing loop to unroll
#pragma warning(disable : 3557)
#endif


#line 22 "sprite.slang"
struct FrameData_0
{
    float time_0;
    float pixelSnap_0;
    float2 viewportSize_0;
};



cbuffer frame_0 : register(b1)
{
    FrameData_0 frame_0;
}

#line 16
struct PushData_0
{
    float4x4 viewProj_0;
    float4 tint_0;
};


#line 29
cbuffer pc_0 : register(b0)
{
    PushData_0 pc_0;
}

#line 9
struct VSOutput_0
{
    float4 position_0 : SV_Position;
    float2 uv_0 : TEXCOORD0;
    float4 color_0 : COLOR0;
};


#line 2
struct VSInput_0
{
    float2 position_1 : POSITION;
    float2 uv_1 : TEXCOORD0;
    float4 color_1 : COLOR0;
};


#line 37
VSOutput_0 vertexMain(VSInput_0 input_0)
{
    VSOutput_0 o_0;

#line 39
    float2 _S1 = input_0.position_1;

#line 39
    float2 p_0;

    if((frame_0.pixelSnap_0) > 0.5f)
    {

#line 41
        p_0 = floor(_S1 + 0.5f);

#line 41
    }
    else
    {

#line 41
        p_0 = _S1;

#line 41
    }

    o_0.position_0 = mul(pc_0.viewProj_0, float4(p_0, 0.0f, 1.0f));
    o_0.uv_0 = input_0.uv_1;
    o_0.color_0 = input_0.color_1 * pc_0.tint_0;
    return o_0;
}

