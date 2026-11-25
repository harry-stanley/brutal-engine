@binding(1) @group(0) var spriteTexture_texture_0 : texture_2d<f32>;

@binding(2) @group(0) var spriteTexture_sampler_0 : sampler;

@binding(0) @group(1) var maskTexture_0 : texture_2d<f32>;

@binding(1) @group(1) var maskSampler_0 : sampler;

struct pixelOutput_0
{
    @location(0) output_0 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) uv_0 : vec2<f32>,
    @location(1) color_0 : vec4<f32>,
};

@fragment
fn fragmentMain( _S1 : pixelInput_0, @builtin(position) position_0 : vec4<f32>) -> pixelOutput_0
{
    ;
    var _S2 : pixelOutput_0 = pixelOutput_0( (textureSample((spriteTexture_texture_0), (spriteTexture_sampler_0), (_S1.uv_0))) * _S1.color_0 * vec4<f32>((textureSample((maskTexture_0), (maskSampler_0), (_S1.uv_0))).x) );
    return _S2;
}

