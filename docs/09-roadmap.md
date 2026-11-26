# 9. Roadmap

v1.0 is delivered through seven milestones. Each one ends with a tagged `0.N` release and a **sample game shipped on Windows and Linux**, the tested targets. macOS is best-effort and unverified from M1 on ([ADR-0015](adr/0015-tested-platforms-windows-linux.md)). Order matters: every milestone builds only on what earlier milestones have merged, and each release ships only its own and earlier milestones' packages ([ADR-0016](adr/0016-trunk-based-milestones.md)).

No dates are committed yet. After M0 we'll have real throughput data and can estimate the rest. M0's spikes exist precisely to retire the riskiest unknowns before any large investment.

---

## M0: Foundations and proof of platform (0.1)

**Goal:** the repo, build, CI and test harness are production-ready, and the riskiest platform assumptions are proven or disproven.

Infrastructure:
- Repo scaffold (layout from [Repository layout](04-repo-layout.md)): `global.json`, `Directory.*.props`, `.editorconfig`, central packages, lock files, LICENSE, CONTRIBUTING, SECURITY, CODE_OF_CONDUCT, PR and issue templates, CODEOWNERS, Dependabot.
- `ci.yml` with the 3-OS matrix, branch protection configured.
- Test harness: xUnit v3 setup and coverage reporting. TestUtilities (headless host stub, in-memory VFS, image diff) moved to M1, where each part lands with the module that needs it.
- `BrutalEngine.Analyzers` skeleton with BE1001 (discarded native result) and its tests.
- `BrutalEngine.Core`: diagnostics, `Result`, generational handles, logging facade, tracking allocators (with tests).

Spikes (throwaway code in a `spikes/` branch, each ending in a written result added to [Risks](10-risks.md) or an ADR):

| ID | Question | Pass criteria |
|----|----------|---------------|
| S1 | SDL3 window + Vulkan 1.3 surface + clear-colour swapchain on all 3 OSes, with **bundled** MoltenVK and loader on macOS | Runs on Windows and Arch Linux on real hardware, and on a GitHub macOS runner with no Vulkan SDK installed (real-Mac testing depends on Q6). Minimum glibc and macOS versions recorded |
| S2 | NativeAOT publish of S1 for all 3 RIDs | Zero trim/AOT warnings from BRUTAL packages, runs identically to JIT |
| S3 | Software Vulkan on GitHub runners: lavapipe (Linux), lavapipe or SwiftShader (Windows), Metal availability for MoltenVK (macOS) | Offscreen render + readback + validation layers working in CI per OS, or a documented fallback |
| S4 | Slang: compile a Slang shader to SPIR-V with `Ahwoo.Brutal.Slang` on all 3 OSes, extract reflection | Cooker-usable API confirmed. Otherwise switch to ShaderC (update ADR-0002) |
| S5 | World model prototype benchmark (once ADR-0006 is decided) | Iteration, spawn/despawn and structural-change costs measured against the targets in ADR-0006 |

Results for S1 to S5 are in [docs/spikes](spikes/README.md). Status at the close of M0 (2026-11-26):

- **S2 is done on all 3 OSes.** The `vulkan-smoke` CI job published HelloWindow and the smoke harness with NativeAOT and ran them next to their JIT builds on Linux, Windows and macOS (#41, [CI run 45599521930](https://github.com/harry-stanley/brutal-engine/actions/runs/45599521930)).
- **S1 is done on Windows and macOS:** windowed on Windows on the reference machine, and offscreen on GitHub's macOS runner. In CI, HelloWindow presented headless on Linux (lavapipe) and macOS (bundled MoltenVK), and windowed on Windows (lavapipe), because no Windows driver offers headless surfaces ([HelloWindow in CI](spikes/README.md#hellowindow-in-ci)).
- **S1 windowed on Arch Linux on real hardware is deferred** to the backlog ([#27](https://github.com/harry-stanley/brutal-engine/issues/27), left open). Decision (maintainer, 2026-11-26): M0 closes without it. Consequence: the minimum glibc and macOS versions that S1's pass criteria ask for aren't recorded, so 0.1 states no minimum OS versions.

**Exit criteria**, each with its close-out note:

- CI green on 3 OSes. **Closed 2026-11-26:** one main run had failed on Linux only, in an allocation test that picked up bytes from a background GC ([run 45622765798](https://github.com/harry-stanley/brutal-engine/actions/runs/45622765798)). #66 made the allocation tests deterministic, and every main run since has passed. The last one before this close-out, at #49's merge ([run 45641448107](https://github.com/harry-stanley/brutal-engine/actions/runs/45641448107)), ran the Windows and macOS smoke in the smoke-delta tier ([CI tiers](06-ci-cd-and-release.md#ci-tiers)), because #49's own run had built and tested the same tree on all 3 OSes ([run 45640964521](https://github.com/harry-stanley/brutal-engine/actions/runs/45640964521)). The last main run that ran every job itself passed on all 3 OSes too ([run 45635378428](https://github.com/harry-stanley/brutal-engine/actions/runs/45635378428)).
- Spikes S1 to S4 resolved. **Closed 2026-11-26:** S2, S3 and S4 pass. S1 passes everywhere except the deferred Arch run above.
- ADR-0006 accepted. **Closed 2026-11-25** (#2).
- `HelloWindow` sample (window, clear colour, BRUTAL splash) published with NativeAOT for all 3 RIDs. **Closed 2026-11-26:** the splash shows before the clear-colour scene since #26, and the push-to-main run at its merge ([run 45626276683](https://github.com/harry-stanley/brutal-engine/actions/runs/45626276683)) ran it JIT and NativeAOT on all 3 OSes and uploaded `hello-window-win-x64`, `hello-window-linux-x64` and `hello-window-osx-arm64`. On a real GPU it ran only on Windows (RTX 2070 SUPER).

**Deviations approved for 0.1.** Decision (maintainer, 2026-11-26): M0 closes with a `v0.1.0-alpha` tag (published as a pre-release) and a GitHub Release that carries the CI `hello-window-<rid>` builds, with no NuGet packages. These items of the [milestone definition of done](08-workflow.md#definition-of-done-for-a-milestone) don't exist yet and are accepted as missing for 0.1:

- **`release.yml`:** not written. M0 was tagged `v0.1.0-alpha` and published by hand as a GitHub pre-release. No package is published, because the first package release needs Ahwoo's OK on the name and package IDs ([Licensing obligations](01-vision-and-scope.md#licensing-obligations)). No milestone schedules the workflow yet.
- **Scripted playthrough on self-hosted real GPUs:** no self-hosted runners exist, and HelloWindow's only scripted run is its CI run on software or paravirtual GPUs. M1 adds a nightly workflow on software Vulkan and records real-GPU runs on the reference machine in each graphics PR. A self-hosted runner is revisited at the end of M1.
- **Manual QA checklist on real hardware:** not written, and no milestone schedules it yet. M0's only real-hardware runs are the Windows ones on the reference machine.
- **Docs site:** there's no docfx site or `docs.yml`. The manual and API reference are [M6](#m6-10-hardening-100) work. Until then, `docs/` is the documentation.

M0 sets no benchmark budgets, so that item doesn't apply.

---

## M1: Core runtime and renderer (0.2)

- `BrutalEngine.Platform` + `Platform.Sdl3`: window, events, DPI, headless mode, VFS, paths.
- `BrutalEngine.Graphics` + `Graphics.Vulkan`: device selection, swapchain (resize, minimise, out-of-date), frames in flight, VMA, staging uploads, textures, buffers, samplers, pipelines, bind groups, dynamic rendering, a minimal frame graph, timestamp queries, debug names. Device-lost handling with fault-injection tests.
- Graphics conformance suite: software Vulkan in CI; real GPUs locally on the reference machine for each graphics PR, recorded in the PR. Decision (maintainer, 2026-11-26): no self-hosted nightly runner in M1, revisited at its close.
- `BrutalEngine.World`: ECS core, schedules, command buffers, hierarchy and transforms, generated queries.
- `BrutalEngine.Runtime`: host lifecycle, fixed-step loop, module registry, splash, crash reports.
- `BrutalEngine.DebugOverlay`: ImGui with our SDL3 platform backend and a renderer on our render interface. Frame-timing and log panels.
- TestUtilities (moved from M0): the in-memory VFS lands with Platform, image diff with the graphics conformance suite, and the headless host stub with Runtime.

**Sample:** `SpriteStress`, a benchmark scene. **Budget:** 100k moving sprites at 60 FPS on min-spec (the renderer here is a simple instanced path, finalised in M2).

---

## M2: 2D essentials (0.3)

- `Rendering2D`: cameras (pixel-perfect and HD), sprite batching and sorting, atlases, materials, debug shapes, render layers/targets.
- `Input`: action maps, contexts, gamepads, rebinding, fixed-step latching, record/replay.
- `Audio` + `Audio.Sdl3`: mixer, buses, DSP basics, streaming music (decoder per spike S6), device change handling.
- `Assets` + `brutal-cook`: GUIDs and `.meta`, importers (images, Aseprite, fonts, audio, shaders, tilemaps), incremental deterministic cooking, packs, async runtime loading, dev-mode hot reload.
- `Text`: FreeType bitmap + SDF, dynamic atlases, layout, rich text.
- `Tilemaps`: chunked rendering, Tiled/LDtk import, auto-tiling.
- Publishing targets for Windows and Linux (the macOS `.app` bundle is best-effort and unverified, [ADR-0015](adr/0015-tested-platforms-windows-linux.md)) and `dotnet new brutalgame` template.

**Sample game 1:** a small complete top-down or platformer game with tilemaps, audio, text, menus via simple sprites, and gamepad support.

---

## M3: Gameplay systems (0.4)

- `Physics2D`: Box2D 3.1 integration, ECS sync, events, queries, job-system integration, tilemap collision generation, determinism tests.
- `Animation`: sprite-sheet clips, property curves, skeletal 2D runtime (our format), state machines.
- `UI`: retained tree, flexbox-subset layout, themes, widgets, gamepad navigation, data binding, IME text fields.
- Localisation: `LString` everywhere, language switching, pseudo-localisation tests. Spike S8 (complex-script shaping) result implemented or documented.

**Sample game 2:** a physics-driven game with skeletal characters, full UI menus and settings, two languages.

---

## M4: Visuals (0.5)

- 2D lighting (point, spot, global), normal maps, shadows from occluders.
- Post-processing chain on the frame graph (bloom, LUT grading, vignette, CRT and pixel-art presets).
- `Particles`: CPU system with modules, sub-emitters and collision. GPU path only if benchmarks require it.

**Sample:** sample game 1 or 2 upgraded with lighting and particles, plus a lighting stress benchmark.

---

## M5: Networking (0.6)

- Spike S7 (transport choice) → ADR.
- Transport, replication with interest management and delta compression, snapshot interpolation, client prediction and reconciliation, network simulator, headless dedicated-server build, fuzzed parsers.

**Sample game 3:** a small 2 to 8 player networked game, playable across Windows and Linux against a dedicated server.

---

## M6: 1.0 hardening (1.0.0)

- Full public API review: naming, consistency, every `PublicAPI.Unshipped.txt` entry moved to Shipped.
- Package validation baseline set. The breaking-change policy takes effect.
- Documentation: manual (getting started, every subsystem, publishing, licence obligations), API reference, all samples documented.
- Performance pass against all budgets on min-spec hardware. Soak tests clean for 7 consecutive nights.
- Security review: asset and packet parsers fuzzed clean, dependency audit, SECURITY.md process tested.
- Signing and notarisation enabled if certificates are available.
- Release `1.0.0`.

---

## After 1.0 (not scheduled)

ImGui-based editor on the same runtime (scene view, inspector, asset browser, play-in-editor), C# hot reload in development, FMOD audio plugin, additional render backends (Metal, D3D12, or SDL_GPU), GPU particles if not done, more platforms.

---

## Open questions to resolve during milestones

| ID | Question | Needed by |
|----|----------|-----------|
| Q1 | ~~World model~~ **Resolved:** ADR-0006 accepted (in-house archetype ECS) | - |
| Q2 | ~~Test hardware~~ **Resolved:** RTX 2070 SUPER on Windows 11 and bare-metal Arch Linux, which becomes the *reference machine* for performance budgets. No min-spec machine is available, so min-spec budgets stay unverified claims until one is (see Q7) | - |
| Q3 | Skeletal animation authoring tool support (own format only, Spine import after licence review, DragonBones, or other) | M3 |
| Q4 | Networking transport (spike S7) | M5 |
| Q5 | Ahwoo's view on the "BrutalEngine" name and NuGet IDs (R9). The maintainer accepts the name for now | Revisit before 1.0 |
| Q6 | macOS hardware: no Mac available. S3 showed GitHub's macos-15 runner can run MoltenVK (paravirtual Metal) for correctness tests, and CI did in M0. [ADR-0015](adr/0015-tested-platforms-windows-linux.md) took macOS out of CI, because nobody can debug a macOS-only failure without a Mac. A real Mac (Mac mini or rented cloud Mac) is still needed for real GPU families, performance, windowed presentation, and `.app` signing/notarisation | Before the first release that claims macOS support (end of M2) |
| Q7 | Min-spec hardware for performance budgets (e.g. an integrated-GPU laptop). Until then, budgets are measured on the reference machine only | Before M6 |
