# ADR-0012: M1 shaders: a Slang-only cook command and committed SPIR-V

- **Status:** Accepted
- **Date:** 2026-11-26
- **Deciders:** the maintainer

## Context

M1 needs SPIR-V before the asset cooker exists: the graphics conformance pipelines, the ImGui overlay renderer, the splash (unless it stays blit-only) and the SpriteStress sample all draw with shaders.

- [ADR-0002](0002-render-interface-vulkan-first.md) decides that shaders are written in Slang and compiled offline to SPIR-V by the cooker. The runtime never parses shader source: it loads cooked SPIR-V plus reflection data ([Architecture 3.8](../03-architecture.md#38-graphics-render-interface-brutalenginegraphics-and-vulkan-backend), docs/03-architecture.md:140).
- The cooker (`brutal-cook`, with importers, packs and `.meta` files) is an M2 deliverable ([Roadmap, M2](../09-roadmap.md#m2-2d-essentials-03), docs/09-roadmap.md:54).
- Spike S4 compiled a realistic sprite shader through `Ahwoo.Brutal.Slang` 2026.10.3 to SPIR-V that is valid under `spirv-val --target-env vulkan1.3`, with full reflection, and the output was byte-identical across compiles. Only Windows was executed; natives ship for `win-x64`, `linux-x64` and `osx-arm64` ([S4 results](../spikes/README.md#s4-slang-offline-shader-compilation), docs/spikes/README.md:77-88).
- S4 also found binding gaps: the reflection layout types are empty structs, so reflection needs raw `Slang.PInvoke` calls, and the blob getters are `internal` (docs/spikes/README.md:84-87).
- The Slang native package is large (about 99 MB, [Tech stack](../02-tech-stack.md#brutal-packages-used), docs/02-tech-stack.md:32). Its unpacked folder in the local NuGet cache, `~/.nuget/packages/ahwoo.brutal.slang.native/2026.10.3/`, is 325 MB (`du -sh`, 2026-11-26).
- `BrutalEngine.Cook` is already in the layer map as an L6 tool (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:47`). Runtime modules never reference tools ([Architecture 3.1](../03-architecture.md#31-layering), docs/03-architecture.md:38).
- `Ahwoo.Brutal.Slang` 2026.10.3 depends on `Ahwoo.Brutal.Strings` 2026.10.2 (its nuspec, `~/.nuget/packages/ahwoo.brutal.slang/2026.10.3/ahwoo.brutal.slang.nuspec`, checked 2026-11-26), so the tool carries the R16 dependency that [ADR-0011](0011-brutal-objectpool-dependency.md) deals with.

## Options considered

1. **Seed `tools/BrutalEngine.Cook` with only a Slang shader command:** the engine's built-in shaders live as `.slang` sources next to their consumer; their cooked SPIR-V is committed and embedded as resources; one CI job on Linux re-cooks them and fails on any byte difference (S4 showed byte-identical output). Pipeline layouts are declared explicitly in C# in M1 (no runtime reflection format yet); M2's cooker adds reflection blobs. Pros: the smallest step that keeps "offline, never at runtime", reuses the planned tool's name and layer, and proves determinism in CI. Cons: built-in pipeline layouts are written by hand in M1 and must match the shaders.
2. **Pull the full M2 shader importer forward** (reflection format, versioned blobs, `.meta`) into M1. Pros: no hand-written layouts. Cons: larger, and it front-loads asset-format decisions that M2 owns.
3. **Compile Slang in an MSBuild step** during every build of the consuming projects. Pros: no committed binaries. Cons: puts the 99 MB native in every engine build and in CI's restore of every job.
4. **GLSL compiled with `Ahwoo.Brutal.ShaderC` at build time** (the fallback in [Tech stack, Shaders](../02-tech-stack.md#shaders)). Cons: contradicts ADR-0002's Slang choice without a reason; S4 passed.

## Decision

Decision (maintainer, 2026-11-26): option 1. In M1, `tools/BrutalEngine.Cook` has one command, which compiles Slang shaders to SPIR-V. The engine's built-in shaders are committed as `.slang` sources with their cooked SPIR-V, which is embedded in the consuming assembly; a Linux CI job re-cooks them and fails on any byte difference; pipeline layouts are declared in C#. ADR-0002 still holds: Slang, compiled offline, never at runtime.

## Consequences

- **Runtime:** consuming assemblies embed SPIR-V and never reference the cook tool or `Ahwoo.Brutal.Slang`. Games never ship a shader compiler ([Tech stack, Shaders](../02-tech-stack.md#shaders)).
- **The tool:** `BrutalEngine.Cook` is created by the first issue that needs a shader, with only the Slang command, and must fail (non-zero exit) on any compile error so CI can rely on it. Its command-line shape is that issue's design. Importers, packs, `.meta` files and the `brutal-cook` dotnet-tool packaging still arrive in M2.
- **Pipeline layouts:** each built-in pipeline declares its bind group layouts, push-constant ranges and vertex inputs in C#. Until M2 adds reflection, the only check that a layout matches its shader is a test: the graphics conformance suite runs each built-in pipeline with Vulkan validation on, so a mismatch fails as a validation error. S4's matrix-layout watch item (`RowMajor` decoration, docs/spikes/README.md:88) needs a GPU round-trip test on the first shader that takes a matrix.
- **CI:** one Linux job restores the tool, re-cooks every built-in `.slang` file and compares the output byte for byte with the committed SPIR-V. Any difference fails the job. Only that job (and local cooking) restores the Slang native package; the 3-OS build and test jobs don't.
- **Cross-OS determinism is unverified.** S4 compared compiles on Windows only. The first PR that adds the Linux re-cook job must show that SPIR-V cooked on Windows and on Linux is byte-identical. If it isn't, the comparison can't pass for SPIR-V cooked on another OS, and that is a trigger to revisit this ADR rather than to loosen the check.
- **Contributors** who change a built-in shader re-run the cook command and commit the new SPIR-V in the same commit as the `.slang` change.
- **Superseded by M2:** when the cooker's shader importer and reflection format land, built-in shaders move to it and C#-declared layouts are replaced by reflection. That change gets its own ADR if it alters anything decided here.
- **Revisit if** Slang output turns out not to be deterministic across compiles, OSes or patch versions, or if hand-declared layouts cause repeated mismatches before M2.
