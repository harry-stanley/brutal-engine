#pragma pack_matrix(column_major)
#ifdef SLANG_HLSL_ENABLE_NVAPI
#include "nvHLSLExtns.h"
#endif

#ifndef __DXC_VERSION_MAJOR
// warning X3557: loop doesn't seem to do anything, forcing loop to unroll
#pragma warning(disable : 3557)
#endif


#line 90 "core"
Texture2D<float4 > spriteTexture_texture_0 : register(t0);


#line 53 "sprite.slang"
SamplerState spriteTexture_sampler_0 : register(s0);


#line 33
Texture2D<float4 > maskTexture_0 : register(t1);


#line 34
SamplerState maskSampler_0 : register(s1);


#line 9
struct VSOutput_0
{
    float4 position_0 : SV_Position;
    float2 uv_0 : TEXCOORD0;
    float4 color_0 : COLOR0;
};


#line 50
float4 fragmentMain(VSOutput_0 input_0) : SV_TARGET
{

#line 50
    float2 _S1 = input_0.uv_0;

    ;

    return spriteTexture_texture_0.Sample(spriteTexture_sampler_0, _S1) * input_0.color_0 * maskTexture_0.Sample(maskSampler_0, input_0.uv_0).x;
}

