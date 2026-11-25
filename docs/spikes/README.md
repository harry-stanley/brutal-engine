# M0 spike results

The spikes from the [Roadmap](../09-roadmap.md) and [Risks](../10-risks.md), run on 2026-11-25. The code is throwaway and lives only on spike branches, never on `main`:

- [`spike/m0-local-spikes`](https://github.com/harry-stanley/brutal-engine/tree/spike/m0-local-spikes/spikes): S1/S2 (Windows), S4, S5, S6/S8. Each folder holds the full raw report.
- [`spike/s3-software-vulkan`](https://github.com/harry-stanley/brutal-engine/tree/spike/s3-software-vulkan/spikes/s3): S3 on GitHub's Linux and Windows runners.
- [`spike/s1-macos-moltenvk`](https://github.com/harry-stanley/brutal-engine/tree/spike/s1-macos-moltenvk/spikes/smac): S1/S3 on GitHub's macOS runner.

Local runs used the reference machine (Windows 11, Ryzen 9 5950X, RTX 2070 SUPER, NVIDIA 621.24). Package versions: `Ahwoo.Brutal.*` 2026.10.x, .NET SDK 10.0.404, runtime 10.0.15.

| Spike | Result | Decision impact |
|-------|--------|-----------------|
| S1 Windows: SDL3 + Vulkan 1.3 swapchain | **Pass** | Platform.Sdl3 / Graphics.Vulkan approach confirmed. Binding gaps listed below |
| S1/S3 macOS: bundled loader + MoltenVK on GitHub runner | **Pass** (offscreen) | MoltenVK bundling works. macOS GPU tests can run on every PR. A real Mac is still needed before release (Q6 updated) |
| S2 NativeAOT | **Pass** on all 3 OSes, 0 trim/AOT warnings: Windows locally, then Windows, Linux and macOS in CI ([CI run 45599521930](https://github.com/harry-stanley/brutal-engine/actions/runs/45599521930), [HelloWindow in CI](#hellowindow-in-ci)) | ADR-0007 confirmed for the code paths exercised |
| S3 Linux + Windows software Vulkan on GitHub runners | **Pass** | Software-Vulkan GPU test jobs can go into `ci.yml` |
| S4 Slang offline compilation | **Pass** | ADR-0002 (Slang) confirmed. ShaderC stays as the fallback |
| S5 ECS prototype | **Partial**: 1 target missed under stress | ADR-0006 stands. Design changes recorded for M1 |
| S6 audio decoding (research) | Recommendation | Decide in an ADR at the start of M2 |
| S8 text shaping (research) | Recommendation | Decide in an ADR at the start of M3 |

---

## S1 + S2: SDL3 window, Vulkan 1.3 swapchain, NativeAOT (Windows)

**What ran:**
- SDL3 resizable Vulkan window. Instance with SDL's extensions, debug utils, the validation layer with synchronization validation, and a counting messenger.
- Vulkan 1.3 device with `dynamicRendering` + `synchronization2`, 2 frames in flight, animated clear.
- Two forced resizes to exercise OUT_OF_DATE and recreation. Full teardown in reverse order.

**Results:**
- JIT: exit 0, 300 frames presented at about 61 fps, 3 swapchain recreations, **0 validation errors or warnings**.
- A deliberate leak run is caught by the layer (exit 1), which proves the checks are live.
- NativeAOT `win-x64`: **0 warnings, 0 errors** with `TrimmerSingleWarn=false`, and behaviour identical to the JIT run. About 4.7 MB shippable output (exe + SDL3.dll + VulkanEx.dll).
- **Caveat:** BRUTAL assemblies don't declare `IsAotCompatible`/`IsTrimmable`, so "0 warnings" covers only the code reached. Every new BRUTAL code path we use must stay covered by AOT smoke tests.

**BRUTAL findings** (checked against the source):
- `AcquireNextImageKHR` and `Queue.PresentKHR` **throw** `VkResultError` on `VK_ERROR_OUT_OF_DATE_KHR`, which is a normal event (`Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs`, around line 8361). Our backend must call these through the raw function table, so OUT_OF_DATE is a return code, not an exception.
- Command-buffer allocation and memory mapping are `internal` in core `Brutal.Vulkan`, so `Brutal.Vulkan.Abstractions` is required.
- `uint64` timeouts and timeline values are bound as signed `nint`. The SDL Vulkan binding covers only 3 functions (no presentation-support query).
- `Ahwoo.Brutal.Strings` depends on **`Microsoft.Extensions.ObjectPool` 11.0.0-preview.5** (verified in the nuspec), and the BRUTAL logging and DI stack pulls in about 24 assemblies. That's a pre-release dependency in a .NET 10 LTS product, so it's tracked as risk R16.

**Not verified:** manual drag-resize, minimise and close; SUBOPTIMAL; visual output, apart from one screenshot of HelloWindow's splash on the RTX 2070 SUPER (#50); long runs; windowed presentation on Linux or macOS; windowed presentation on any real GPU other than the RTX 2070 SUPER. S1's last criterion, windowed on Arch Linux on real hardware with the minimum glibc and macOS versions recorded, is deferred to the backlog ([#27](https://github.com/harry-stanley/brutal-engine/issues/27)). Since the spike, CI covers windowed presentation on Windows lavapipe with two resizes, and headless presentation on Linux and macOS ([HelloWindow in CI](#hellowindow-in-ci)).

## S1 / S3: macOS on GitHub's `macos-15` runner

**Runner:** macOS 15.8.2 VM, Apple Paravirtual Metal device (Mac2 family, no Apple7+ or Metal 3).

**Results:**
- MoltenVK 1.4.2 (pinned release asset) plus a Vulkan-Loader built from a pinned tag, **bundled, with no SDK or Homebrew**. The device reports API 1.3.357.
- With `VK_KHR_portability_subset`, `dynamicRendering` and `synchronization2` enabled, an offscreen clear and readback matched 4096/4096 pixels.
- Validation layers, built from source and loaded from a bundled folder, report 0 errors. A deliberate canary error is caught.

**Two required engine behaviours:**
1. BRUTAL's resolver (`NativeLibrary.TryLoad(..., DllImportSearchPath.LegacyBehavior, ...)` in `Brutal.Vulkan/ImportResolver.cs`) **doesn't search the app directory**. The loader is found when shipped as a RID-specific asset (`runtimes/osx-arm64/native/`) with a RID-specific publish, or when the engine preloads it by absolute path. Engine plan: ship the natives as NuGet RID assets, and have the backend preload the loader explicitly.
2. The bundled loader doesn't find the bundled MoltenVK ICD by itself. The engine must set `VK_DRIVER_FILES` (or `VK_ADD_DRIVER_FILES`) to the bundled ICD JSON before `vkCreateInstance`.

**Costs:** the smoke job takes about 1 min. A validation-layer source build takes 6.5 to 13 min, so it must be cached.

**Limits:** the paravirtual GPU gives correctness, not real GPU families or performance. Not tested: windowed presentation, pipelines and shaders, `.app` bundles and signing.

## HelloWindow in CI

After the spikes, the `vulkan-smoke` CI job ([CI/CD](../06-ci-cd-and-release.md#ciyml-every-pr-and-every-push-to-main-plus-nightly), step 4) took over S1 and S2 for `samples/HelloWindow` (#41, [CI run 45599521930](https://github.com/harry-stanley/brutal-engine/actions/runs/45599521930)). On each OS it runs the JIT build and the NativeAOT publish with validation, plus negative tests (an injected validation error must exit 2, a bad option must exit 64). In that run, each presented 120 frames; the job presents more since the splash landed (#50):

| | ubuntu-24.04 | windows-2025 | macos-15 |
|---|---|---|---|
| Driver | lavapipe | lavapipe (Mesa for Windows) | MoltenVK from the CI bundle; the NativeAOT publish has it next to the executable |
| Presentation | headless (`VK_EXT_headless_surface`) | windowed in the runner's desktop session, two resizes | headless (`VK_EXT_headless_surface`) |
| Result | pass, 0 validation messages, 0 trim/AOT warnings | same | same, with no Vulkan SDK or Homebrew Vulkan. The NativeAOT publish also runs with no `--vulkan-loader` and no `VK_DRIVER_FILES`; the JIT build gets the bundled loader passed explicitly |

**Finding (Windows):** no Windows driver offers `VK_EXT_headless_surface`. Neither the NVIDIA driver on the reference machine nor Mesa lavapipe 26.3.1 for Windows lists it (checked with `vulkaninfo` for #24). SDL's offscreen video driver needs it to create a Vulkan surface, so on Windows `--headless` fails with a clear message, and CI runs HelloWindow windowed instead. Linux and macOS headless runs were the first time `--headless` presented anywhere.

This is software and paravirtual GPU coverage only. Real-GPU runs are the Windows ones on the reference machine.

## S3: software Vulkan on GitHub's Linux and Windows runners

| | ubuntu-24.04 | windows-2025 |
|---|---|---|
| Driver | lavapipe, Mesa 25.2.8 (apt `mesa-vulkan-drivers`) | lavapipe, Mesa 26.3.1 (pal1000 `mesa-dist-win`, SHA-256 pinned) |
| Loader + layers | apt `libvulkan1` 1.3.275, `vulkan-validationlayers` 1.3.275 | LunarG SDK 1.4.363.1, silent install, SHA-256 pinned |
| Setup / total job | about 30 s / about 1 min | about 32 s / about 75 s |
| Result | 64x64 clear + readback exact, 0 validation messages, negative test caught | same |

**Findings:**
- **Hosted images ship the .NET 11 SDK.** Without a `global.json`, .NET 10 code silently runs on .NET 11. Our root `global.json` prevents this for engine code.
- Windows: BRUTAL loads `vulkan-1.dll` from System32 only, so on Windows the loader always comes from the driver or SDK (that matches the plan).
- Khronos publishes Windows validation-layer binaries only as a mutable nightly. Use the LunarG SDK instead.
- Linux (1.3.275) and Windows (1.4.363) validation-layer versions differ, so validation coverage differs between OSes.

## S4: Slang offline shader compilation

- `Ahwoo.Brutal.Slang` 2026.10.3 (Slang 2026.7). Natives ship for exactly `win-x64`, `linux-x64` and `osx-arm64`. Only Windows was executed.
- A realistic sprite shader (push constants, UBO, combined and separate samplers, three vertex inputs) compiled to SPIR-V that is **valid under `spirv-val --target-env vulkan1.3`**.
- Full reflection: set, binding and type for every descriptor, the 80-byte push-constant layout with offsets, vertex locations mapped to VkFormats, and entry points with stages.
- Diagnostics are `error[E…] --> path:line:col` with source excerpts. Output is byte-identical across compiles.
- MSL, HLSL and WGSL are also generated from the same source (MSL not compiled with Apple tools).
- **Binding gaps:**
  - The reflection layout types are empty structs, so the cooker needs raw `Slang.PInvoke` calls.
  - The blob getters are `internal` (`Brutal.Slang/Generated/Structs.gen.cs:313`).
  - String parameters need trailing NULs.
- **Watch item:** the push-constant matrix is decorated `RowMajor` even when column-major was requested. The renderer needs a GPU round-trip test for the matrix convention.

## S5: archetype ECS prototype (ADR-0006)

**Prototype:**
- Generational ids, unmanaged location map, 256-bit component masks, an add/remove edge cache, and swap-remove.
- Three SoA storage variants: 16 KB chunks, 64 KB chunks and a growable table.
- Cached queries, per-chunk change versions, and an unmanaged command buffer.
- Parallel queries on BRUTAL `JobScheduler`.
- A randomised reference-model test.

Results (BenchmarkDotNet, reference machine only):

| ADR-0006 target | Result |
|-----------------|--------|
| Iterate 1M entities x 2 components < 1 ms | **Pass** for chunk-span loops (531 to 695 µs), SIMD (136 to 234 µs) and parallel with 16 workers (139 to 178 µs). **Fail** for a per-entity `ref` pair enumerator (1.31 ms) |
| Spawn + despawn 100k / frame via command buffer < 5 ms | **Marginal**: 4.3 ms in spawn order. **Fail** when the despawn order is shuffled (about 6 ms) and at p99 (5.7 to 6.4 ms) |
| Zero managed allocations | **Pass**: 0 B in every benchmark and in 1,000-frame runs, including the parallel path |

**Design consequences for M1** (full detail in the spike's `DESIGN-NOTES.md`):
- 64 KB pooled chunks by default (16 KB is 18 to 31% slower here).
- Chunk loops or generator-rewritten systems as the primary query API, not per-entity enumerators.
- Batched or sorted command-buffer playback before the API is frozen.
- An explicit Pending state for reserved entities.
- A **world-owned component registry filled by generated code**, because a static `TypeId<T>` breaks the hot-reload rule (architecture 3.21).

**Not done:** hierarchy and transform propagation, the source generator, a NativeAOT publish, min-spec hardware (Q7).

## S6: compressed audio decoding (research, nothing executed)

- **Recommendation:** use the SDL3_mixer that BRUTAL already ships (`Ahwoo.Brutal.Sdl.Mixer`, SDL_mixer 3.2.4) as a **decoder only**, feeding our own mixer:
  - Streamed music: a headless `MIX_CreateMixer` + `MIX_Track` per stream, pulled with `MIX_Generate` on a worker into a lock-free ring buffer.
  - One-shot and cook-time decodes: `MIX_CreateAudioDecoder_IO` + `MIX_DecodeAudio`.
- Vorbis, FLAC, MP3 and WAV are included. No new natives or licences are needed.
- **Opus is not in BRUTAL's build** (checked in the binaries and notices). Leave it out of v1. If needed later, the first candidate is Concentus (managed, BSD-3).
- Not yet executed: AOT, allocation behaviour and runtime tests.

## S8: complex-script text shaping (research, nothing executed)

- BRUTAL's SDL3_ttf 3.2.2 contains HarfBuzz 8.5.0, but exports only `TTF_*`. Shaped output is reachable only through SDL_ttf's own layout (integer positions, no BiDi), so it's suitable only as a fallback.
- `Ahwoo.Brutal.FreeType` ships FreeType 2.14.3, with no HarfBuzz and no managed bindings, so we write our own `FT_*` imports.
- **Recommendation:**
  - Write our own small `LibraryImport` bindings to HarfBuzz's C API (about 25 functions), with BiDi, script itemisation and fallback handled in our layout code.
  - Get natives from the MIT `HarfBuzzSharp.NativeAssets.*` packages first, then build HarfBuzz per RID in our own natives workflow.
- Not yet executed: runtime tests, AOT, macOS export check.
