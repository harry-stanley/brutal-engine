# Spike S4: offline Slang → SPIR-V + reflection through Ahwoo.Brutal.Slang

Date: 2026-11-25. Host: Windows 11 x64, .NET SDK 10.0.404 (pinned in `app/global.json`), Vulkan SDK 1.4.363.0 (spirv-val/spirv-dis).
Timebox: about 25 min. **Everything below was executed on Windows (win-x64) only.** linux-x64 and osx-arm64 were checked only by listing the package contents. Nothing ran on those platforms.

## Verdict
**Keep Slang (ADR-0002).** On win-x64 the BRUTAL binding compiles a realistic 2D sprite shader (VS+FS) to SPIR-V that passes spirv-val with `--target-env vulkan1.3`. It gives complete reflection: descriptor set/binding/type, push-constant size and member offsets, vertex input locations with formats, entry points and stages. Diagnostics have file, line and column and are readable. Output is byte-identical across runs. The same source also emits MSL, HLSL and WGSL. Keep ShaderC as the documented fallback. It works too, but it has **no reflection API**, so choosing it would mean adding SPIRV-Reflect or SPIRV-Cross, which BRUTAL does not ship. Treat the binding gaps below as cooker-side wrapper work, not blockers.

## Versions
| Item | Version |
|---|---|
| Ahwoo.Brutal.Slang | 2026.10.3 (latest; also 2026.10.1 exists) |
| Ahwoo.Brutal.Slang.Native | 2026.10.3 (only version) |
| Native Slang (`IGlobalSession.GetBuildTag()`) | **2026.7** |
| Ahwoo.Brutal.ShaderC / .Native | 2026.10.3 / 2026.10.3 |
| Ahwoo.Brutal.Core / Strings | 2026.10.4 / 2026.10.2 |
| Vulkan SDK slangc (for comparison only) | 2026.17 (`Bin/slang-standard-module-2026.17`) |

Note: BRUTAL's Slang (2026.7) is 10 releases older than the SDK's slangc (2026.17). Don't mix the two in the cooker. Cook only through the pinned package so the output stays reproducible.

## Task 1: RIDs and native files (listed with Python zipfile, never extracted)
Ahwoo.Brutal.Slang.Native 2026.10.3 (nupkg 99,972,457 B):

| RID | Files | Unpacked |
|---|---|---|
| win-x64 | slang.dll (60 KB loader), slang-compiler.dll (30.5 MB), slang-glslang.dll (9.8 MB), slang-glsl-module.dll (1.3 MB) | 41.7 MB |
| linux-x64 | libslang.so, libslang-compiler.so, libslang-compiler.so.0.2026.7 (**3 identical 31.6 MB copies**: symlinks flattened), libslang-glslang-2026.7.so, libslang-glsl-module-2026.7.so | 105.8 MB |
| osx-arm64 | libslang.dylib, libslang-compiler.dylib, libslang-compiler.0.2026.7.dylib (3 × 27.5 MB copies), libslang-glslang-2026.7.dylib, libslang-glsl-module-2026.7.dylib | 92.6 MB |

Also included: `build/natives/lib/win-x64/{slang,slang-compiler}.lib`, LICENSE/NOTICE and third-party notices.

Ahwoo.Brutal.ShaderC.Native 2026.10.3 (16.8 MB):

| RID | Files | Unpacked |
|---|---|---|
| win-x64 | shaderc_shared.dll, SPIRV-Tools-shared.dll | 6.6 MB |
| linux-x64 | libshaderc_shared.so (+ .so.1 copy), libSPIRV-Tools-shared.so | 23.6 MB |
| osx-arm64 | libshaderc_shared.dylib (+ .1 copy), libSPIRV-Tools-shared.dylib | 19.5 MB |

Confirmed: win-x64, linux-x64 and osx-arm64 for both packages. There is no osx-x64, linux-arm64 or win-arm64. That matters only for the cooker host, because games never ship the compiler. Raw listing: `rids.txt`.

## Task 2: Slang → SPIR-V + reflection (works)
Program: `app/S4Slang.cs` (a file-based .NET 10 app run with `dotnet run S4Slang.cs -- out sprite.slang`, exit 0). Shader: `app/sprite.slang`. It has a vertex input struct (POSITION/TEXCOORD0/COLOR0), a `[[vk::push_constant]] ConstantBuffer<PushData>` (float4x4 viewProj + float4 tint), a UBO, a combined `Sampler2D` and a separate `Texture2D` + `SamplerState` in set 1.
It uses the legacy compile-request flow from HelloSlang.cs: `CreateCompileRequest`, `SetCodeGenTarget(Spirv)`, `SetTargetProfile(FindProfile("spirv_1_5"))`, `SetMatrixLayoutMode(ColumnMajor)`, two `AddEntryPoint` calls, `Compile`, `GetEntryPointCode(ep)`, and reflection through `GetReflection()` plus raw `Slang.PInvoke.spReflection*`.

Output: `out/sprite.vert.spv` (1812 B) and `out/sprite.frag.spv` (1076 B). **spirv-val --target-env vulkan1.3: both VALID.** SPIR-V 1.5, entry point name "main" in each module (which matches the `Presets.ShaderStage` "main" assumption).

Reflection dump (`out/reflection.txt`, produced by the program):
```
== Global parameters (5)
  pc: kind=ConstantBuffer category=PushConstantBuffer bindingType=PushConstant pushConstantSize=80 offset=0
      +0 viewProj : matrix size=64
      +64 tint : vector size=16
  frame: kind=ConstantBuffer category=DescriptorTableSlot bindingType=ConstantBuffer set=0 binding=0 uboSize=16
      +0 time : float size=4
      +4 pixelSnap : float size=4
      +8 viewportSize : vector size=8
  spriteTexture: kind=Resource category=DescriptorTableSlot bindingType=CombinedTextureSampler set=0 binding=1
  maskTexture: kind=Resource category=DescriptorTableSlot bindingType=Texture set=1 binding=0
  maskSampler: kind=SamplerState category=DescriptorTableSlot bindingType=Sampler set=1 binding=1
== Entry points (2)
  vertexMain stage=Vertex
    param input kind=Struct
      location=0 position : POSITION0 type=vector scalar=Float32 cols=2 -> R32G32_SFLOAT
      location=1 uv : TEXCOORD0 type=vector scalar=Float32 cols=2 -> R32G32_SFLOAT
      location=2 color : COLOR0 type=vector scalar=Float32 cols=4 -> R32G32B32A32_SFLOAT
  fragmentMain stage=Pixel        (Stage.Pixel == Stage.Fragment, Enumerations.gen.cs:398,414)
```
spirv-dis agrees: same DescriptorSet/Binding decorations, Locations 0/1/2 and OpTypeSampledImage for the combined sampler. `spReflection_ToJson` (Slang.gen.cs:1168) also works and gave a 17.8 KB JSON (`out/reflection.json`). That could serve as the cooker's reflection format directly, or as a cross-check.
Mapping a type to a VkFormat is the cooker's job; Slang gives only the scalar type and column count.

## Task 3: diagnostics (good)
`Compile()` returns a failing Result (0x80004005 E_FAIL). `GetDiagnosticOutput()` returns Rust-style text with **path:line:col**, error code, a source excerpt and a caret. Output from `out/diag-unknown-member.txt`:
```
error[E30027]: member not found
  --> C:/game/shaders/sprite.slang:44:18
   |
44 | o.uv = input.uvv;
   |              ^^^ 'uvv' is not a member of 'VSInput'.
---'
```
A syntax error gives `E20002 ... sprite.slang:53:31`. A missing `;` is reported at the next token (`46:5 unexpected identifier, expected ';'`), as most compilers do. Quirks: the excerpt strips leading indentation, though the column still counts it; and every message ends with a stray `---'` line (native Slang 2026.7 output). The result code is always generic E_FAIL, so the cooker should parse the `--> path:line:col` lines or use `SetDiagnosticCallback`, which was not tested.

## Task 4: Metal (MSL) from the same source (generates; not compiled on Apple)
The `CompileTarget.Metal` request with the same source and entry points succeeded: `out/sprite.vert.metal` 2973 B, `out/sprite.frag.metal` 1504 B. It produces `[[fragment]]`, `[[stage_in]]`, `texture2d<float, access::sample>` and `[[texture(n)]]/[[sampler(n)]]`, and splits the combined Sampler2D into a texture plus a sampler. HLSL and WGSL targets also succeeded. **Not verified:** that the MSL compiles with Apple's `metal` toolchain or runs (no Mac available), and Metal-side reflection/argument indices. The output has `#line` directives; use `SetLineDirectiveMode` for release cooks.

## Task 5: ShaderC comparison (works, no reflection)
`app/S4ShaderC.cs` compiles an equivalent GLSL 450 VS/FS (push_constant, UBO, sampler2D, texture2D+sampler) with `SetTargetEnv(Vulkan, Vulkan_1_3)`: vert 2160 B, frag 1232 B, both **VALID** in spirv-val vulkan1.3, and byte-identical on a second compile. Diagnostics come as glslang-style `path:line: error: ...` with no column; a missing `;` is reported on the following line. **Brutal.ShaderC exposes no reflection.** BRUTAL also has no SPIRV-Reflect or SPIRV-Cross binding, so the fallback needs an extra dependency or a hand-written SPIR-V reflector. ShaderC can't target MSL either; that would need SPIRV-Cross.

## Task 6: determinism (yes, within this test)
SPIR-V bytes were identical across (a) two requests on the same global session and (b) a request on a freshly created second global session. SHA-256 prefixes: vert FBAFEAF04D48F4A6, frag B5C937DA6A0F4E12. Not tested: separate processes or machines, other OSes, `-g` debug info, absolute vs relative source paths (the path is embedded in diagnostics and possibly in debug info).

## Binding bugs and gaps (BRUTAL source, read-only)
1. **Reflection is raw C only.** `ProgramLayout`, `EntryPointLayout`, `ShaderReflection` and `EntryPointReflection` are empty handle structs (Brutal.Slang/Generated/Structs.gen.cs:1363, 1368, 1153, 1175), so every program/entry-point query is `Slang.PInvoke.spReflection_*`. The variable- and type-layout structs do have methods (Structs.gen.cs:892-940, 504+). The global-using aliases are confusing: `Reflection = ProgramLayout`, `ProgramLayout = ShaderReflection`, `ReflectionEntryPoint = EntryPointLayout`, `EntryPointLayout = EntryPointReflection` (Generated/GlobalUsings.gen.cs:55-62).
2. **The blob getters are internal.** `GetEntryPointCodeBlob` and `GetTargetCodeBlob` are `internal` on `ICompileRequest` (Structs.gen.cs:313, 323). The public `GetEntryPointCode` only covers target 0, so a multi-target request has to call `Slang.PInvoke.spGetEntryPointCodeBlob` (Slang.gen.cs:349) and handle an `nint` blob. `spReflection_ToJson` also returns an `nint` blob, and `Slang.WrapComInstance` is `internal` (Slang.cs:160). The spike read that blob through the raw COM vtable (slots 3/4/2). Workaround used: one request per target.
3. **RefString8 must be NUL-terminated.** It is passed as a raw pointer (Brutal.Strings/String8/RefString8Marshaller.cs:18-21; `RefString8(Span<Char8>)` at RefString8.cs:66 does no check). `"x"u8` literals are safe, but runtime strings (file paths, shader source) must carry a trailing `\0` outside the span or native code over-reads. This is easy to get wrong in a cooker.
4. `SlangException` keeps only the generic Result plus the text (SlangException.cs:7-28). There are no structured diagnostics.
5. The HelloSlang.cs:18 comment ("GC releases the global session") is wrong: Slang.cs:9-13 and 183-185 say the GC never releases proxies. The spike calls `Slang.Release(gs)` explicitly.
6. **Matrix convention needs a GPU test.** With `SetMatrixLayoutMode(ColumnMajor)` and `mul(pc.viewProj, v)`, the SPIR-V marks the push-constant matrix `RowMajor` with MatrixStride 16. This looks like Slang's documented row/column naming flip for SPIR-V, but the engine must pin one convention and check it with a GPU round-trip test (System.Numerics.Matrix4x4 upload). Not verified here.
7. Packaging: the Linux and macOS natives store flattened symlinks as full copies (3 × 31.6 MB on linux-x64), which only affects cooker download size. Windows loads its companion DLLs explicitly (Slang.cs:35-44). Only the Windows load path was exercised.

## Recommendation for the cooker (ADR-0002 stands)
- Use Ahwoo.Brutal.Slang 2026.10.3 pinned (native 2026.7). Use one compile request per target (SPIR-V, later MSL) with both entry points. Emit `.spv` plus reflection, either the spike's own model or `spReflection_ToJson`. Run spirv-val in CI.
- Wrap three things in the cooker: NUL-terminated string helper, blob reading, and diagnostic parsing (`--> path:line:col`).
- Keep ShaderC/GLSL as the fallback. If it's ever needed, also budget for a SPIR-V reflection library.
- Follow-ups not covered by this spike: run the cooker on linux-x64 and osx-arm64; compile the MSL with Xcode; run the matrix-convention GPU test; check determinism across processes and machines; look at the modern `ISession`/`IComponentType` API (ISessionExtensions.cs) for `import` and module caching.

## Artifacts
- `app/S4Slang.cs`, `app/sprite.slang`, `app/S4ShaderC.cs`, `app/global.json`
- `app/out/` (spv, metal, hlsl, wgsl, reflection.txt/json, diag-*.txt), `app/out-shaderc/`
- `app/run-slang.log`, `app/run-shaderc.log`, `rids.txt`, `tools/listpkg.py`
- nupkgs (untrusted, listing only): `nupkg-slang/`, `nupkg-shaderc/`
