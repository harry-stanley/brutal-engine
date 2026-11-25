// S4 spike: Slang -> SPIR-V / MSL + reflection via Ahwoo.Brutal.Slang.
#:property AllowUnsafeBlocks=true
#:package Ahwoo.Brutal.Slang@2026.10.3
#:package Ahwoo.Brutal.Core@2026.10.4
#:package Ahwoo.Brutal.Strings@2026.10.2

using System.Security.Cryptography;
using System.Text;
using Brutal.SlangApi;

var outDir = args.Length > 0 ? args[0] : "out";
var srcPath = args.Length > 1 ? args[1] : "sprite.slang";
Directory.CreateDirectory(outDir);
var source = File.ReadAllText(srcPath);

var gs = Slang.CreateGlobalSession();
Console.WriteLine($"Slang build tag: {gs.GetBuildTag()}");

// ---- 1. SPIR-V + reflection
var spv = Compiler.Compile(gs, source, "sprite.slang", CompileTarget.Spirv, reflect: true, out var refl, out var diag1);
File.WriteAllBytes(Path.Combine(outDir, "sprite.vert.spv"), spv[0]);
File.WriteAllBytes(Path.Combine(outDir, "sprite.frag.spv"), spv[1]);
File.WriteAllText(Path.Combine(outDir, "reflection.txt"), refl);
Console.WriteLine($"SPIR-V: vert {spv[0].Length} B, frag {spv[1].Length} B; diagnostics: '{diag1.Trim()}'");
Console.WriteLine(refl);

// ---- 2. Determinism: same global session, new request; then a fresh global session
var spv2 = Compiler.Compile(gs, source, "sprite.slang", CompileTarget.Spirv, false, out _, out _);
var gs2 = Slang.CreateGlobalSession();
var spv3 = Compiler.Compile(gs2, source, "sprite.slang", CompileTarget.Spirv, false, out _, out _);
for (int i = 0; i < 2; i++)
    Console.WriteLine($"Determinism ep{i}: sameSession={spv[i].AsSpan().SequenceEqual(spv2[i])} freshGlobalSession={spv[i].AsSpan().SequenceEqual(spv3[i])} sha256={Convert.ToHexString(SHA256.HashData(spv[i]))[..16]}");

// ---- 3. Metal (MSL) from the same source
try
{
    var msl = Compiler.Compile(gs, source, "sprite.slang", CompileTarget.Metal, false, out _, out var mdiag);
    File.WriteAllBytes(Path.Combine(outDir, "sprite.vert.metal"), msl[0]);
    File.WriteAllBytes(Path.Combine(outDir, "sprite.frag.metal"), msl[1]);
    Console.WriteLine($"MSL: vert {msl[0].Length} B, frag {msl[1].Length} B; diagnostics: '{mdiag.Trim()}'");
}
catch (Exception e) { Console.WriteLine($"MSL FAILED: {e.Message}"); }

// Also HLSL + WGSL for breadth (cheap)
foreach (var (t, ext) in new[] { (CompileTarget.Hlsl, "hlsl"), (CompileTarget.Wgsl, "wgsl") })
{
    try
    {
        var o = Compiler.Compile(gs, source, "sprite.slang", t, false, out _, out _);
        File.WriteAllBytes(Path.Combine(outDir, $"sprite.vert.{ext}"), o[0]);
        File.WriteAllBytes(Path.Combine(outDir, $"sprite.frag.{ext}"), o[1]);
        Console.WriteLine($"{t}: vert {o[0].Length} B, frag {o[1].Length} B");
    }
    catch (Exception e) { Console.WriteLine($"{t} FAILED: {e.Message}"); }
}

// ---- 4. Diagnostics quality
var bad1 = source.Replace("o.uv = input.uv;", "o.uv = input.uvv;");                 // unknown member
var bad2 = source.Replace("float mask = maskTexture", "float mask = maskTexture +");  // nonsense expr
var bad3 = source.Replace("o.color = input.color * pc.tint;", "o.color = input.color * pc.tint");  // missing ;
foreach (var (name, bad) in new[] { ("unknown-member", bad1), ("bad-expression", bad2), ("missing-semicolon", bad3) })
{
    try
    {
        Compiler.Compile(gs, bad, "C:/game/shaders/sprite.slang", CompileTarget.Spirv, false, out _, out _);
        Console.WriteLine($"DIAG {name}: unexpectedly compiled");
    }
    catch (SlangException e)
    {
        Console.WriteLine($"DIAG {name}: SlangException.Result=0x{(uint)e.Result.Value:X8}");
        File.WriteAllText(Path.Combine(outDir, $"diag-{name}.txt"), e.Message);
    }
}

// Release order: children (requests already destroyed) -> global sessions
Slang.Release(gs2);
Slang.Release(gs);
Console.WriteLine("done");

static unsafe class Compiler
{
    public static byte[][] Compile(IGlobalSession gs, string source, string path, CompileTarget target, bool reflect,
        out string reflection, out string diagnostics)
    {
        using var req = gs.CreateCompileRequest();
        req.SetCodeGenTarget(target);
        if (target == CompileTarget.Spirv)
            fixed (byte* prof = "spirv_1_5"u8) req.SetTargetProfile(0, gs.FindProfile(prof));
        req.SetMatrixLayoutMode(MatrixLayoutMode.ColumnMajor);
        req.SetOptimizationLevel(OptimizationLevel.Default);
        var tu = req.AddTranslationUnit(SourceLanguage.Slang, "sprite"u8);
        req.AddTranslationUnitSourceString(tu, Z(path), Z(source));
        var vs = req.AddEntryPoint(tu, "vertexMain"u8, Stage.Vertex);
        var fs = req.AddEntryPoint(tu, "fragmentMain"u8, Stage.Fragment);
        var r = req.Compile();
        diagnostics = req.GetDiagnosticOutput();
        if (r.IsFailure)
            throw new SlangException(r, diagnostics);
        var outputs = new byte[2][];
        foreach (var ep in new[] { vs, fs })
        {
            var p = req.GetEntryPointCode(ep, out var size);
            if (p == null) throw new InvalidOperationException($"null code for ep {ep}");
            outputs[ep] = new ReadOnlySpan<byte>(p, checked((int)(ulong)size)).ToArray();
        }
        reflection = reflect ? Reflect(req) : "";
        return outputs;
    }

    static ReadOnlySpan<byte> Z(string s) => Encoding.UTF8.GetBytes(s + "\0").AsSpan()[..^1].ToArray().Concat(new byte[] { 0 }).ToArray().AsSpan()[..^1];

    static string Reflect(ICompileRequest req)
    {
        var sb = new StringBuilder();
        var layout = req.GetReflection();
        uint n = Slang.PInvoke.spReflection_GetParameterCount(layout);
        sb.AppendLine($"== Global parameters ({n})");
        for (uint i = 0; i < n; i++)
        {
            var p = Slang.PInvoke.spReflection_GetParameterByIndex(layout, i);
            var name = p.GetVariable().GetName();
            var tl = p.GetTypeLayout();
            var kind = tl.GetKind();
            var cat = tl.GetParameterCategory();
            uint binding = Slang.PInvoke.spReflectionParameter_GetBindingIndex(p);
            uint set = Slang.PInvoke.spReflectionParameter_GetBindingSpace(p);
            long ranges = (long)Slang.PInvoke.spReflectionTypeLayout_getBindingRangeCount(tl);
            var btype = ranges > 0 ? tl.GetBindingRangeType(0) : BindingType.Unknown;
            sb.Append($"  {name}: kind={kind} category={cat} bindingType={btype}");
            if (cat == ParameterCategory.PushConstantBuffer)
            {
                var el = tl.GetElementTypeLayout();
                sb.Append($" pushConstantSize={(ulong)el.GetSize(ParameterCategory.Uniform)} offset={(ulong)p.GetOffset(ParameterCategory.PushConstantBuffer)}");
                DumpFields(sb, el);
            }
            else
            {
                sb.Append($" set={set} binding={binding}");
                if (kind == TypeKind.ConstantBuffer)
                {
                    var el = tl.GetElementTypeLayout();
                    sb.Append($" uboSize={(ulong)el.GetSize(ParameterCategory.Uniform)}");
                    DumpFields(sb, el);
                }
                else sb.AppendLine();
            }
        }

        ulong epc = Slang.PInvoke.spReflection_getEntryPointCount(layout);
        sb.AppendLine($"== Entry points ({epc})");
        for (ulong e = 0; e < epc; e++)
        {
            var ep = Slang.PInvoke.spReflection_getEntryPointByIndex(layout, e);
            var stage = Slang.PInvoke.spReflectionEntryPoint_getStage(ep);
            sb.AppendLine($"  {Slang.PInvoke.spReflectionEntryPoint_getName(ep)} stage={stage}");
            uint pc = Slang.PInvoke.spReflectionEntryPoint_getParameterCount(ep);
            for (uint k = 0; k < pc; k++)
            {
                var par = Slang.PInvoke.spReflectionEntryPoint_getParameterByIndex(ep, k);
                var ptl = par.GetTypeLayout();
                sb.AppendLine($"    param {par.GetVariable().GetName()} kind={ptl.GetKind()}");
                if (stage == Stage.Vertex && ptl.GetKind() == TypeKind.Struct)
                {
                    for (uint f = 0; f < ptl.GetFieldCount(); f++)
                    {
                        var fl = ptl.GetFieldByIndex(f);
                        var ft = fl.GetTypeLayout().GetType();
                        sb.AppendLine($"      location={(ulong)fl.GetOffset(ParameterCategory.VaryingInput)} {fl.GetVariable().GetName()} : {fl.GetSemanticName()}{(ulong)fl.GetSemanticIndex()} type={ft.GetName()} scalar={ft.GetScalarType()} cols={ft.GetColumnCount()} -> {VkFormat(ft.GetScalarType(), ft.GetColumnCount())}");
                    }
                }
            }
        }

        // Full JSON dump via spReflection_ToJson (blob read through raw COM vtable).
        var hr = Slang.PInvoke.spReflection_ToJson(layout, req, out nint blob);
        if (((Result)hr).IsSuccess && blob != 0)
        {
            var vtbl = *(void***)blob;
            var getPtr = (delegate* unmanaged[Stdcall]<nint, void*>)vtbl[3];
            var getSize = (delegate* unmanaged[Stdcall]<nint, nuint>)vtbl[4];
            var release = (delegate* unmanaged[Stdcall]<nint, uint>)vtbl[2];
            var json = Encoding.UTF8.GetString((byte*)getPtr(blob), (int)getSize(blob));
            release(blob);
            File.WriteAllText("out/reflection.json", json);
            sb.AppendLine($"== spReflection_ToJson: {json.Length} chars written to out/reflection.json");
        }
        else sb.AppendLine($"== spReflection_ToJson failed: {hr}");
        return sb.ToString();
    }

    static void DumpFields(StringBuilder sb, TypeLayoutReflection el)
    {
        sb.AppendLine();
        for (uint f = 0; f < el.GetFieldCount(); f++)
        {
            var fl = el.GetFieldByIndex(f);
            var ftl = fl.GetTypeLayout();
            sb.AppendLine($"      +{(ulong)fl.GetOffset(ParameterCategory.Uniform)} {fl.GetVariable().GetName()} : {ftl.GetType().GetName()} size={(ulong)ftl.GetSize(ParameterCategory.Uniform)}");
        }
    }

    static string VkFormat(ScalarType s, uint cols) => (s, cols) switch
    {
        (ScalarType.Float32, 1) => "R32_SFLOAT",
        (ScalarType.Float32, 2) => "R32G32_SFLOAT",
        (ScalarType.Float32, 3) => "R32G32B32_SFLOAT",
        (ScalarType.Float32, 4) => "R32G32B32A32_SFLOAT",
        (ScalarType.UInt32, 1) => "R32_UINT",
        (ScalarType.Int32, 1) => "R32_SINT",
        _ => $"?({s}x{cols})",
    };
}
