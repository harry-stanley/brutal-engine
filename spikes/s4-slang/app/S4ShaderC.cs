// S4 spike: GLSL -> SPIR-V via Ahwoo.Brutal.ShaderC (fallback comparison).
#:property AllowUnsafeBlocks=true
#:package Ahwoo.Brutal.ShaderC@2026.10.3
#:package Ahwoo.Brutal.Core@2026.10.4
#:package Ahwoo.Brutal.Strings@2026.10.2

using System.Text;
using Brutal.ShaderCApi;

var outDir = args.Length > 0 ? args[0] : "out-shaderc";
Directory.CreateDirectory(outDir);

const string vert = """
#version 450
layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec2 inUv;
layout(location = 2) in vec4 inColor;
layout(location = 0) out vec2 vUv;
layout(location = 1) out vec4 vColor;
layout(push_constant) uniform PushData { mat4 viewProj; vec4 tint; } pc;
layout(set = 0, binding = 0) uniform FrameData { float time; float pixelSnap; vec2 viewportSize; } frame;
void main()
{
    vec2 p = inPosition;
    if (frame.pixelSnap > 0.5) p = floor(p + 0.5);
    gl_Position = pc.viewProj * vec4(p, 0.0, 1.0);
    vUv = inUv;
    vColor = inColor * pc.tint;
}
""";
const string frag = """
#version 450
layout(location = 0) in vec2 vUv;
layout(location = 1) in vec4 vColor;
layout(location = 0) out vec4 outColor;
layout(set = 0, binding = 1) uniform sampler2D spriteTexture;
layout(set = 1, binding = 0) uniform texture2D maskTexture;
layout(set = 1, binding = 1) uniform sampler maskSampler;
void main()
{
    vec4 c = texture(spriteTexture, vUv);
    float mask = texture(sampler2D(maskTexture, maskSampler), vUv).r;
    outColor = c * vColor * mask;
}
""";

using var compiler = ShaderC.CreateCompiler();
using var options = ShaderC.CreateCompilerOptions();
options.SetTargetEnv(TargetEnv.Vulkan, (uint)EnvVersion.Vulkan_1_3);

byte[] Build(string src, ShaderKind kind, string name)
{
    using var r = compiler.Compile(Z(src), kind, Z(name), "main"u8, options);
    unsafe { return new ReadOnlySpan<byte>(r.GetBytes(), checked((int)r.GetLength().Value)).ToArray(); }
}

var v = Build(vert, ShaderKind.VertexShader, "sprite.vert");
var f = Build(frag, ShaderKind.FragmentShader, "sprite.frag");
File.WriteAllBytes(Path.Combine(outDir, "sprite.vert.spv"), v);
File.WriteAllBytes(Path.Combine(outDir, "sprite.frag.spv"), f);
Console.WriteLine($"ShaderC SPIR-V: vert {v.Length} B, frag {f.Length} B");
var v2 = Build(vert, ShaderKind.VertexShader, "sprite.vert");
Console.WriteLine($"ShaderC determinism vert: {v.AsSpan().SequenceEqual(v2)}");

try { Build(vert.Replace("vUv = inUv;", "vUv = inUvv;"), ShaderKind.VertexShader, "C:/game/shaders/sprite.vert"); }
catch (ShaderException e) { Console.WriteLine($"ShaderC DIAG ({e.GetType().Name}):\n{e.Message}"); }
try { Build(vert.Replace("vColor = inColor * pc.tint;", "vColor = inColor * pc.tint"), ShaderKind.VertexShader, "C:/game/shaders/sprite.vert"); }
catch (ShaderException e) { Console.WriteLine($"ShaderC DIAG ({e.GetType().Name}):\n{e.Message}"); }

// RefString8 needs NUL termination; keep the terminator outside the span.
static ReadOnlySpan<byte> Z(string s) { var b = Encoding.UTF8.GetBytes(s + "\0"); return b.AsSpan(0, b.Length - 1); }
