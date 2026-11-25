# 2. Tech stack

Every dependency below is pinned in `Directory.Packages.props` (central package management) and restored in locked mode (`packages.lock.json`). Dependency updates arrive as Dependabot PRs and go through the full CI matrix like any other change.

Versions marked *(verified)* were checked against the published NuGet packages on 2026-11-25.

## Language and runtime

| Item | Choice | Why |
|------|--------|-----|
| Runtime | .NET 11 (`net11.0`) | Supported until 2028-11-14, the same day as .NET 10 LTS. BRUTAL's `net10.0` packages run on it. [ADR-0014](adr/0014-dotnet-11.md), superseding [ADR-0001](adr/0001-dotnet-10-lts.md) |
| SDK | .NET 11 SDK, pinned in `global.json` (`11.0.100`, `rollForward: latestPatch`). Dependabot proposes SDK bumps | Reproducible builds: CI and contributors use the same feature band, and only patch (servicing) updates roll forward automatically. A missing SDK gives a clear error instead of a silent drift |
| Language | C# 15 (the .NET 11 default) | `unsafe`, function pointers, `ref struct`, generic math and `field` keyword all available |
| Release publishing | NativeAOT, trimmed | Fast startup, no JIT, single native executable. JIT for development. [ADR-0007](adr/0007-nativeaot-release.md) |

Development machines need the .NET 11 SDK in the pinned `11.0.1xx` feature band (11.0.100 or a later patch) and the Vulkan SDK for validation layers (1.4.363 installed on the maintainer's machine).

## BRUTAL packages used

All BRUTAL packages are published as `Ahwoo.Brutal.*` with CalVer versions (2026.10.x at the time of writing). Native libraries come in `*.Native` sub-packages for exactly `win-x64`, `linux-x64` and `osx-arm64`.

| Package | Used for | Natives *(verified)* |
|---------|----------|----------------------|
| `Ahwoo.Brutal.Core`, `.Interop`, `.Memory`, `.Collections`, `.Strings` | Foundations: pointers, native handles, allocators, non-allocating collections and strings | managed only |
| `Ahwoo.Brutal.Numerics`, `.Maths` | SIMD maths inside a module. Not in public signatures: public maths types (`Vector2`, `Matrix3x2` and so on) come from the BCL's `System.Numerics` ([ADR-0014](adr/0014-dotnet-11.md)) | managed only |
| `Ahwoo.Brutal.Concurrency` | Job scheduler for parallel systems | managed only |
| `Ahwoo.Brutal.Logging` | Bridged into the engine logger by the backends (M1) | managed only |
| `Ahwoo.Brutal.Localization` | String tables, `LString` formatting | managed only |
| `Ahwoo.Brutal.Sdl` (+ `.Native`) | Windows, events, input, gamepads, audio device, Vulkan surface (`SDL_Vulkan_CreateSurface`) | SDL 3.4 (`libSDL3.so.0.4.12`), 3 RIDs |
| `Ahwoo.Brutal.Vulkan` (+ `.Native`) | Vulkan 1.3 bindings and function tables | 3 RIDs (helper library, not the loader) |
| `Ahwoo.Brutal.Vulkan.Vma` (+ `.Native`) | GPU memory allocation | 3 RIDs |
| `Ahwoo.Brutal.Slang` (+ `.Native`) | Offline shader compilation in the cooker (tools only, never shipped in games). In M1, `tools/BrutalEngine.Cook` has only a Slang shader command; built-in shaders' SPIR-V is committed, and a Linux CI job re-cooks it and fails on any byte difference ([ADR-0012](adr/0012-m1-shader-cooking.md)) | Native package is ~99 MB. 3 RIDs (spike S4) |
| `Ahwoo.Brutal.FreeType` | Font rasterisation (cook time and runtime dynamic glyphs) | 3 RIDs |
| `Ahwoo.Brutal.Stb` (+ `.Native`) | Image decoding (PNG, TGA and others) at cook time | 3 RIDs |
| `Ahwoo.Brutal.Ktx` (+ `.Native`) | KTX2 GPU texture containers (compressed textures for HD assets) | 3 RIDs |
| `Ahwoo.Brutal.Box2D` (+ `.Native`) | 2D physics (M3) | Box2D 3.1.1, 3 RIDs |
| `Ahwoo.Brutal.ImGui` (+ `.Native`) | Debug overlay now, editor later, with our own SDL3 platform backend and a renderer on our render interface. Not `.ImGui.Backends`: it pulls in GLFW and OpenGL ([ADR-0013](adr/0013-imgui-without-brutal-backends.md)) | 3 RIDs |
| `Ahwoo.Brutal.RenderDoc` | Optional in-engine RenderDoc capture trigger (dev builds) | |
| `Ahwoo.Brutal.Sdl.Ttf` | Candidate for complex-script text shaping (HarfBuzz) in localisation. Decided in spike S8 | 3 RIDs |
| `Ahwoo.Brutal.RakNet` | Candidate networking transport. Decided in spike S7 | 3 RIDs |

Not used: GLFW (SDL3 replaces it, [ADR-0003](adr/0003-sdl3-platform.md)), OpenGL, OpenAL and SDL_mixer (our own mixer, [ADR-0004](adr/0004-audio.md)), Jolt, glTF and MeshOptimizer (3D), FMOD (an optional plugin after v1), NotCurses.

### Not bundled by BRUTAL: we ship these ourselves

| Item | Platform | Plan |
|------|----------|------|
| Vulkan loader (`libvulkan.1.dylib`) and MoltenVK (`libMoltenVK.dylib` + ICD JSON) | macOS | Pinned upstream builds (MoltenVK release asset + SHA-256, Vulkan-Loader by tag and commit, pinned in `natives/macos-vulkan`) from our own `natives` workflow, shipped as `runtimes/osx-arm64/native/` NuGet assets and signed later. Proven on GitHub's macOS runner in M0 ([S1/S3 results](spikes/README.md#s1--s3-macos-on-githubs-macos-15-runner)); CI has run nothing on macOS since [ADR-0015](adr/0015-tested-platforms-windows-linux.md), so it's unverified from M1 on. Two engine duties: BRUTAL's resolver doesn't search the app directory, so the Vulkan backend **preloads the loader by absolute path**; and the bundled loader doesn't find MoltenVK by itself, so the backend **sets `VK_DRIVER_FILES`** to the bundled ICD before creating the instance |
| Vulkan loader | Windows, Linux | Provided by the GPU driver (Windows) or the distro package `libvulkan1` (Linux). Documented as a runtime requirement, checked at startup with a clear error |

## Shaders

- Source language: **Slang**, compiled **offline** by the asset cooker into SPIR-V for the Vulkan backend.
- Why Slang over GLSL via ShaderC: one source can target SPIR-V, Metal Shading Language and HLSL/DXIL, which keeps the future Metal and D3D backends cheap. Slang also supports modules and generics, useful for the 2D material system.
- Games never ship a shader compiler. Cooked shader blobs carry a hash of the source and compiler version, so stale shaders are detected at load time.
- Fallback: if Slang turns out to be unsuitable (spike S4), use `Ahwoo.Brutal.ShaderC` with GLSL, and change ADR-0002 to match.

## Engine-side libraries (ours, no third-party code)

The ECS / world model (pending [ADR-0006](adr/0006-world-model.md)), the render interface and Vulkan backend, the 2D renderer, audio mixer, asset database and pack format, input mapping, UI, animation runtimes and particles are all written in this repository. They are the core value of the engine, and they must meet the allocation and determinism rules, which third-party C# libraries generally don't.

## Serialisation

- **JSON for text formats** (project files, scenes in development, importer metadata), using `System.Text.Json` with **source-generated** `JsonSerializerContext`. That is AOT-safe, with no reflection.
- **Binary for cooked data**: our own versioned, little-endian, chunked format, read through spans with no per-object allocation.
- Component and asset serialisers are produced by our Roslyn **source generator** (`BrutalEngine.Generators`), never by runtime reflection.

## Compression

`System.IO.Compression` (Deflate and Brotli) for pack chunks in v1. That adds no native dependency. A faster codec (LZ4 or Zstandard) can be added later as a pack-format version bump if load-time profiling justifies it.

## Development and test tooling

| Tool | Purpose |
|------|---------|
| xUnit v3 on Microsoft.Testing.Platform | Unit and integration tests |
| CsCheck | Property-based tests (maths, serialisation round-trips, allocators, pack format) |
| SharpFuzz + libFuzzer | Fuzzing asset readers and network packet parsers (nightly) |
| BenchmarkDotNet | Micro and system benchmarks, with nightly regression tracking |
| Microsoft.Testing.Extensions.CodeCoverage | Coverage, published to the PR summary |
| Microsoft.CodeAnalysis.PublicApiAnalyzers | Tracks every public API change in `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` |
| .NET package validation (`EnablePackageValidation`, baseline version) | Binary compatibility checks against the last release (from 1.0) |
| Roslyn analyzers: .NET analyzers at `AnalysisLevel=latest-recommended`, plus our own `BrutalEngine.Analyzers` | Engine-specific rules (allocation in hot paths, unchecked native results, static mutable state) |
| `dotnet format` | Formatting gate in CI, driven by `.editorconfig` |
| MinVer | Package versions taken from git tags |
| docfx | API reference and manual site, published to GitHub Pages |
| Vulkan SDK (validation layers) | Required for development and GPU tests |
| Mesa lavapipe / SwiftShader | Software Vulkan drivers for GPU tests on CI runners without GPUs |
| RenderDoc | Frame debugging |

## Rejected alternatives (summary)

| Option | Rejected because |
|--------|------------------|
| Staying on .NET 10 (LTS) | The target until [ADR-0014](adr/0014-dotnet-11.md) ([ADR-0001](adr/0001-dotnet-10-lts.md)). Its support ends on the same day as .NET 11's (2028-11-14), so staying gains no support time. Revisit the target when .NET 12 LTS ships |
| GLFW | Weaker gamepad, haptics and IME support, and no audio. SDL3 covers windowing, input and audio with one dependency |
| OpenGL backend | Deprecated on macOS, and a second legacy backend would split testing effort |
| SDL_GPU as the only backend | Simpler, but it hides control we need (explicit memory and synchronisation, timestamp queries). It is still a candidate *additional* backend behind our interface, since BRUTAL's SDL3 bindings expose it (`SDL_CreateGPUDevice`) and it reaches Metal and D3D12 natively |
| FMOD as primary audio | Licence fees for larger budgets, natives not redistributable through BRUTAL, and a closed-source core dependency for an open engine |
| Reflection-based serialisation | Incompatible with NativeAOT trimming |
