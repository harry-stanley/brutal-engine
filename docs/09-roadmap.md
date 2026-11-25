# 9. Roadmap

v1.0 is delivered through seven milestones. Each one ends with a tagged `0.N` release and a **sample game shipped on all three OSes**. Order matters: every milestone builds only on what earlier milestones have finished.

No dates are committed yet. After M0 we'll have real throughput data and can estimate the rest. M0's spikes exist precisely to retire the riskiest unknowns before any large investment.

---

## M0: Foundations and proof of platform (0.1)

**Goal:** the repo, build, CI and test harness are production-ready, and the riskiest platform assumptions are proven or disproven.

Infrastructure:
- Repo scaffold (layout from [Repository layout](04-repo-layout.md)): `global.json`, `Directory.*.props`, `.editorconfig`, central packages, lock files, LICENSE, CONTRIBUTING, SECURITY, CODE_OF_CONDUCT, PR and issue templates, CODEOWNERS, Dependabot.
- `ci.yml` with the 3-OS matrix, branch protection configured.
- Test harness: xUnit v3 setup, TestUtilities (headless host stub, in-memory VFS, image diff), coverage reporting.
- `BrutalEngine.Analyzers` skeleton with BE1001 (discarded native result) and its tests.
- `BrutalEngine.Core`: diagnostics, `Result`, generational handles, logging facade, tracking allocators (with tests).

Spikes (throwaway code in a `spikes/` branch, each ending in a written result added to [Risks](10-risks.md) or an ADR):

| ID | Question | Pass criteria |
|----|----------|---------------|
| S1 | SDL3 window + Vulkan 1.3 surface + clear-colour swapchain on all 3 OSes, with **bundled** MoltenVK and loader on macOS | Runs on all 3 real machines with no SDK installed on the Mac. Minimum glibc and macOS versions recorded |
| S2 | NativeAOT publish of S1 for all 3 RIDs | Zero trim/AOT warnings from BRUTAL packages, runs identically to JIT |
| S3 | Software Vulkan on GitHub runners: lavapipe (Linux), lavapipe or SwiftShader (Windows), Metal availability for MoltenVK (macOS) | Offscreen render + readback + validation layers working in CI per OS, or a documented fallback |
| S4 | Slang: compile a Slang shader to SPIR-V with `Ahwoo.Brutal.Slang` on all 3 OSes, extract reflection | Cooker-usable API confirmed. Otherwise switch to ShaderC (update ADR-0002) |
| S5 | World model prototype benchmark (once ADR-0006 is decided) | Iteration, spawn/despawn and structural-change costs measured against the targets in ADR-0006 |

**Exit criteria:** CI green on 3 OSes. Spikes S1 to S4 resolved. ADR-0006 accepted. `HelloWindow` sample (window, clear colour, BRUTAL splash) published with NativeAOT for all 3 RIDs.

---

## M1: Core runtime and renderer (0.2)

- `BrutalEngine.Platform` + `Platform.Sdl3`: window, events, DPI, headless mode, VFS, paths.
- `BrutalEngine.Graphics` + `Graphics.Vulkan`: device selection, swapchain (resize, minimise, out-of-date), frames in flight, VMA, staging uploads, textures, buffers, samplers, pipelines, bind groups, dynamic rendering, a minimal frame graph, timestamp queries, debug names. Device-lost handling with fault-injection tests.
- Graphics conformance suite (software Vulkan in CI, real GPUs nightly).
- `BrutalEngine.World`: ECS core, schedules, command buffers, hierarchy and transforms, generated queries.
- `BrutalEngine.Runtime`: host lifecycle, fixed-step loop, module registry, splash, crash reports.
- `BrutalEngine.DebugOverlay`: ImGui with our SDL3 platform backend and a renderer on our render interface. Frame-timing and log panels.

**Sample:** `SpriteStress`, a benchmark scene. **Budget:** 100k moving sprites at 60 FPS on min-spec (the renderer here is a simple instanced path, finalised in M2).

---

## M2: 2D essentials (0.3)

- `Rendering2D`: cameras (pixel-perfect and HD), sprite batching and sorting, atlases, materials, debug shapes, render layers/targets.
- `Input`: action maps, contexts, gamepads, rebinding, fixed-step latching, record/replay.
- `Audio` + `Audio.Sdl3`: mixer, buses, DSP basics, streaming music (decoder per spike S6), device change handling.
- `Assets` + `brutal-cook`: GUIDs and `.meta`, importers (images, Aseprite, fonts, audio, shaders, tilemaps), incremental deterministic cooking, packs, async runtime loading, dev-mode hot reload.
- `Text`: FreeType bitmap + SDF, dynamic atlases, layout, rich text.
- `Tilemaps`: chunked rendering, Tiled/LDtk import, auto-tiling.
- Publishing targets for all 3 platforms (`.app` bundle on macOS) and `dotnet new brutalgame` template.

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

**Sample game 3:** a small 2 to 8 player networked game, playable across all three OSes against a dedicated server.

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
| Q1 | World model: accept ADR-0006 as proposed or choose an alternative | Before M1 |
| Q2 | Exact GPU models and Mac model of the self-hosted test machines (to set min-spec budgets) | M0 |
| Q3 | Skeletal animation authoring tool support (own format only, Spine import after licence review, DragonBones, or other) | M3 |
| Q4 | Networking transport (spike S7) | M5 |
| Q5 | Ahwoo's view on the "BrutalEngine" name and NuGet IDs (R9) | Before first nuget.org publish (end of M0) |
