# 5. Testing strategy

"Fully tested" has a concrete meaning here: every behaviour a game can depend on is exercised by an automated test that runs in CI on all three operating systems, at the cheapest layer that can actually prove it.

## Test layers

| Layer | What | Where it runs | Gate |
|-------|------|---------------|------|
| Unit | Pure logic: maths, containers, allocators, ECS storage and queries, layout, mixer DSP on buffers, serialisers, pack format, input mapping | Every PR, all 3 OSes | Required |
| Property-based (CsCheck) | Invariants over random inputs: serialise→deserialise identity, allocator never overlaps, ECS add/remove sequences match a reference model, layout constraints | Every PR | Required |
| Native-boundary | Real BRUTAL natives: SDL headless init, Box2D worlds, FreeType rasterisation, Vulkan instance/device on a software driver | Every PR, all 3 OSes | Required |
| Graphics conformance | The render-interface contract: every object, format, pass, barrier case and error path. One suite, run against every backend | Every PR on software Vulkan. Nightly on real GPUs | Required |
| Golden image | Rendered frames compared to reference PNGs with a tolerance-based perceptual diff (per-channel threshold + max differing-pixel ratio). On failure, the diff image is uploaded as a CI artifact | Every PR (lavapipe on Linux). Nightly on all configurations | Required on Linux. Others monitored, then promoted to required once stable |
| Integration | Headless engine boot → load cooked scene → run N fixed steps → assert world state, audio commands, render packet contents. Cook→load round trips | Every PR, all 3 OSes | Required |
| Allocation | Steady-state reference scenes run 1,000 frames. Total managed allocations across all engine threads must be **0 bytes** | Every PR | Required |
| Leak | Debug tracking allocators and GPU resource tracker must be empty at shutdown in every integration and conformance test | Every PR (built into the test harness) | Required |
| Determinism | Record inputs, replay, compare world state hashes per tick: same run twice, and across OSes (hashes uploaded and compared in a final job) | Every PR | Required (single-OS). Cross-OS monitored at first |
| AOT smoke | `tests/BrutalEngine.Aot.Smoke`: published with NativeAOT per RID (trim/AOT warnings are errors) and run, along with its JIT build. The `vulkan-offscreen` scenario clears and reads back an image with validation, on lavapipe (Linux, Windows) and on bundled MoltenVK (macOS), and has a negative test that must exit 2. The same job runs `samples/HelloWindow` for 120 frames with validation, with its JIT build and NativeAOT publish: headless on Linux (lavapipe) and macOS (MoltenVK), and windowed on Windows (lavapipe), where no driver offers `VK_EXT_headless_surface`, with two window resizes. Each has a negative test that must exit 2, and the JIT build a bad-option test that must exit 64. The macOS publish runs self-contained, with the bundled loader, MoltenVK and ICD manifest and no `--vulkan-loader` or `VK_DRIVER_FILES`. The published output is uploaded per RID. Later: every sample | Every PR on Linux. Windows and macOS in the full tier (native-relevant PRs, any change under `samples/`, pushes to `main`, nightly), see [CI tiers](06-ci-cd-and-release.md#ci-tiers). Nightly: all samples | Required |
| Fuzz | Asset readers, pack TOC, text/JSON importers, network packet parsers (SharpFuzz) | Nightly, 30 min per target. Crashes become regression tests | Monitored |
| Benchmarks | BenchmarkDotNet suites plus frame-time budgets for reference scenes (e.g. 100k sprites) | Nightly, results stored and compared against a baseline | A >10% regression opens an issue automatically |
| Soak | Sample games played by a scripted input bot for 60 minutes: memory flat, no errors, no leaks | Nightly on self-hosted machines | Monitored |
| Manual QA | Release-candidate checklist on real hardware (multi-monitor, DPI change, gamepad hot-plug, sleep/resume, audio device switch) | Before each release | Release gate |

## GPU testing without GPUs

GitHub-hosted runners have no usable GPUs. GPU tests therefore run on software Vulkan drivers:

- **Linux:** Mesa **lavapipe** (`mesa-vulkan-drivers`) plus the Vulkan validation layers. This is the primary golden-image platform. Reference images are generated here.
- **Windows:** lavapipe from pal1000 `mesa-dist-win` (pinned release + SHA-256) with the loader and layers from a silently installed, SHA-256-pinned LunarG Vulkan SDK (S3).
- **macOS:** the macos-15 runner exposes a paravirtual Metal device, and our bundled MoltenVK + loader run on it with validation layers (S3). macOS GPU **correctness** tests therefore run on every PR. Performance and real GPU families still need real hardware (Q6 in the [Roadmap](09-roadmap.md)). The `vulkan-smoke` job runs the AOT smoke harness there (JIT and NativeAOT) with the natives a game bundles, pinned in `natives/macos-vulkan`: the Khronos loader, which the harness preloads by absolute path (`--vulkan-loader`), and MoltenVK, selected with `VK_DRIVER_FILES` and required with `--require-driver Moltenvk`. `--require-cpu` doesn't apply, because MoltenVK reports the paravirtual GPU as an integrated GPU. The validation layers are built from source on macOS (about 10 minutes), so the build is cached by `.github/actions/setup-macos-vulkan`, keyed on the pinned commits and the build script.
- **Real GPUs:** self-hosted runners on the maintainer's hardware (see [CI/CD](06-ci-cd-and-release.md#self-hosted-runners)) run the full conformance and golden suites nightly, with real drivers: NVIDIA RTX 2070 SUPER on Windows 11 and on a bare-metal Arch Linux install.

Golden images are produced on lavapipe. Real-GPU runs compare against the same references with a slightly wider tolerance, because rasterisation rules allow small differences. Any test needing a wider tolerance than the global default must say why in a comment.

**Validation layers are mandatory** in every GPU test. A debug-messenger callback turns any validation error or warning (except an explicit, commented allow-list) into a test failure, reported with the message and the object names.

## Headless platform testing

SDL runs with `SDL_VIDEO_DRIVER=offscreen` (or `dummy`) and `SDL_AUDIO_DRIVER=dummy` (or `disk`, to capture output) in CI. Graphics tests render to offscreen targets, so they need no window or swapchain. Swapchain behaviour (resize, minimise, out-of-date) is covered by the conformance suite through a mock surface layer *and* by the self-hosted real-window tests.

## Audio testing

The mixer is a pure function of (commands, time) → sample buffers, so it is tested by rendering to memory and comparing against reference buffers within a tolerance. Tests also check that the real-time callback path never allocates or locks: debug builds instrument it, and a stress test runs 256 voices with parameter churn.

## Architecture tests

`BrutalEngine.Architecture.Tests` loads all engine assemblies and asserts:

- the layering in [Architecture 3.1](03-architecture.md#31-layering), so no assembly references a higher layer,
- no public type outside the L2 backends exposes a type from a `Brutal.*Api` binding namespace,
- no static mutable fields of non-primitive types in engine assemblies (hot-reload readiness).

It also fails when an engine library under `src/` isn't in its layer map or isn't referenced by the test project, so a new module can't escape the rules. The map, the exact rules and the judgement calls are in [the design note](design/architecture-tests.md).

Every public type and member must also be in `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` and have XML docs. The build already enforces that (RS0016/RS0017 and CS1591 are errors), so the architecture tests don't repeat it.

## Coverage

- Line and branch coverage are collected on every PR and shown in the job summary.
- **Floor:** 85% line coverage for engine logic (excluding generated bindings code and thin backend shims, which are covered by conformance and integration tests). Coverage **may not decrease** in a PR without a written justification.
- Coverage is a signal, not the goal. Reviews check that tests assert behaviour, not just execute lines.

## Test conventions

- Test project names mirror the project under test: `src/BrutalEngine.X` → `tests/BrutalEngine.X.Tests`.
- Test names describe behaviour: `Spawn_InsideQuery_IsDeferredUntilSyncPoint`.
- No sleeps or wall-clock timing in tests. Time is injected through the engine clock abstraction.
- No test depends on another test's state or on run order. Tests that need native singletons (SDL init) use collection fixtures.
- Flaky tests are treated as bugs: quarantined with an issue link within one day, and fixed or deleted within one milestone.
- Every bug fix includes a regression test that failed before the fix.
