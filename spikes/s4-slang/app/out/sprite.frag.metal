#include <metal_stdlib>
#include <metal_math>
#include <metal_texture>
using namespace metal;

#line 90 "core"
struct pixelOutput_0
{
    float4 output_0 [[color(0)]];
};


#line 90
struct pixelInput_0
{
    float2 uv_0 [[user(TEXCOORD)]];
    float4 color_0 [[user(COLOR)]];
};


#line 90
struct KernelContext_0
{
    texture2d<float, access::sample> spriteTexture_texture_0;
    sampler spriteTexture_sampler_0;
    texture2d<float, access::sample> maskTexture_0;
    sampler maskSampler_0;
};


#line 50 "sprite.slang"
[[fragment]] pixelOutput_0 fragmentMain(pixelInput_0 _S1 [[stage_in]], float4 position_0 [[position]], texture2d<float, access::sample> spriteTexture_texture_1 [[texture(0)]], sampler spriteTexture_sampler_1 [[sampler(0)]], texture2d<float, access::sample> maskTexture_1 [[texture(1)]], sampler maskSampler_1 [[sampler(1)]])
{

#line 50
    thread KernelContext_0 kernelContext_0;

#line 50
    (&kernelContext_0)->spriteTexture_texture_0 = spriteTexture_texture_1;

#line 50
    (&kernelContext_0)->spriteTexture_sampler_0 = spriteTexture_sampler_1;

#line 50
    (&kernelContext_0)->maskTexture_0 = maskTexture_1;

#line 50
    (&kernelContext_0)->maskSampler_0 = maskSampler_1;

    ;

#line 52
    pixelOutput_0 _S2 = { (((&kernelContext_0)->spriteTexture_texture_0).sample(((&kernelContext_0)->spriteTexture_sampler_0), (_S1.uv_0))) * _S1.color_0 * float4((((&kernelContext_0)->maskTexture_0).sample(((&kernelContext_0)->maskSampler_0), (_S1.uv_0))).x)  };

    return _S2;
}

