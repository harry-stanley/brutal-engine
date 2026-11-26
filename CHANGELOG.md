# Changelog

All notable changes to BrutalEngine are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until 1.0.0, versions are `0.y.z` and breaking changes can happen between minor releases. They are always listed under "Breaking", with migration notes. See [CI/CD and release: Versioning](docs/06-ci-cd-and-release.md#versioning).

Every pull request adds its user-visible changes to the `Unreleased` section.

## [Unreleased]

### Added

- `BrutalEngine.Graphics`, the render interface package, with a typed GPU handle per object kind: `BufferHandle`, `TextureHandle`, `TextureViewHandle`, `SamplerHandle`, `ShaderModuleHandle`, `SwapchainHandle`, `BindGroupLayoutHandle`, `PipelineLayoutHandle`, `BindGroupHandle`, `GraphicsPipelineHandle`, `ComputePipelineHandle`, and `DebugLabel` for interned pass and debug group names. Each is a non-owning value over a generational Core `Handle`; `default` is the null handle, and a handle of one kind never converts to another ([render interface design](docs/design/graphics.md#1-object-model-handles-ownership-and-destruction)).

### Breaking

- The engine targets .NET 11 (`net11.0`) instead of .NET 10 ([ADR-0014](docs/adr/0014-dotnet-11.md), superseding ADR-0001). `global.json` pins SDK 11.0.100 (`rollForward: latestPatch`). Migration: games and tools that reference BrutalEngine projects or packages target `net11.0` and build with the .NET 11 SDK. BRUTAL's `net10.0` packages are used unchanged; NativeAOT builds use the 11.0.0 compiler packs.

### Added

- `BrutalEngine.Platform`, the backend-neutral platform abstraction (no backend yet): `IWindow` (sizes in window coordinates, pixels and content units, state, mode, display, text input and relative mouse mode, and setters that return `Result`), `IVulkanSurfaceSource` (the instance extensions a window needs and `CreateVulkanSurface`, using only `nint` and spans), and the value types `WindowId`, `WindowSize`, `DisplayId`, `WindowState`, `WindowMode` and `DisplayMode` ([platform design](docs/design/platform.md)).
- `BrutalEngine.Core`: `ThreadAffinity`, which captures an owner thread and checks that a call is on it (`ThrowIfNotCurrentThread` runs in every build, and is a null check plus one integer comparison that allocates nothing when it passes), and `WrongThreadException`, thrown with the member's name and both managed thread ids when the check fails.
- `BrutalEngine.World` (new package, the world model of ADR-0006): `WorldOptions`, validated when a world is created (chunk size, initial entity capacity and limit, component-type width, hierarchy depth, clock and name, each named in the exception), and the `EcsWorld` shell: construction, owner-thread checks that throw `WrongThreadException` naming the member, and idempotent disposal. The world never owns or disposes an allocator it is given. `Entity`: an 8-byte generational handle (index plus generation, `Entity.Null` as the default, a raw 64-bit value that round-trips and rejects non-canonical zero-generation values, and `Entity(12v3)` formatting).

### Changed

- `Microsoft.Extensions.ObjectPool`, which BRUTAL's `Ahwoo.Brutal.Strings` brings in as the pre-release `11.0.0-preview.5.26302.115`, is pinned centrally to the 11.0.0 GA release ([ADR-0011](docs/adr/0011-brutal-objectpool-dependency.md)), so `samples/HelloWindow` and its builds ship the GA package. The smoke harness checks at start that BRUTAL's string-builder pool runs on it, and an architecture test keeps `Ahwoo.Brutal.Strings` and the pool out of Core, Platform, Graphics and World.
- Package versions: builds after the `v0.1.0-alpha` tag are versioned `0.2.0-alpha.0.N` (N = commits since the tag) until `v0.2.0` is tagged, instead of `0.1.0-alpha.N`.
- CI builds, tests and smoke-runs on Windows and Linux only. macOS is best-effort and unverified ([ADR-0015](docs/adr/0015-tested-platforms-windows-linux.md)), so there is no `hello-window-osx-arm64` artifact any more. The macOS code, the `osx-arm64` RID, the pinned natives and the macOS setup action are kept.
- `samples/HelloWindow` logs through `BrutalEngine.Core`'s `Logger` and `ConsoleLogSink` (categories `HelloWindow` and `HelloWindow.Vulkan`) instead of plain console writes, so each line now reads `2026-11-26T01:02:03.456Z INFO [HelloWindow] message`. Messages may be up to 64 KiB, so a failure's full exception report stays whole. The validation messenger keeps its counts per instance and reaches them through the callback's user data, with no static state. CI's splash check matches the new line format.
- `samples/HelloWindow`'s published output, and so the `hello-window-<rid>` CI artifacts, no longer include XML doc files (`HelloWindow.xml`, `BrutalEngine.Core.xml`). CI checks there are none.

### Fixed

- `Logger.SetLevel` no longer allocates after a garbage collection. It validated the level with `Enum.IsDefined`, which rebuilds a weakly held runtime cache (240 bytes) on its first call after any collection.
- `samples/HelloWindow`: `--require-driver MaxEnum` is rejected (it's Vulkan's sentinel value, not a driver), an unnamed `VkResult` appears once in error messages instead of twice, a failure inside an SDL callback is reported with its stack trace and the inner exception, and on the splash's 1:1 copy path the window's minimum size is recomputed when the pixel density changes (for example, after a move to another display).

## [0.1.0-alpha] - 2026-11-26

### Added

- Engineering plan in `docs/`: vision and scope, tech stack, architecture, repository layout, testing strategy, CI/CD and release, coding standards, development workflow, roadmap and risks.
- Architecture decision records ADR-0001 to ADR-0010 in `docs/adr/`, with an ADR template. ADR-0006 (archetype ECS world model) is accepted.
- Commit rules: Conventional Commits, small focused commits that each build and pass tests, and squash-merge-only PRs.
- Test hardware and self-hosted runner plan for real-GPU testing.
- README, MIT licence and BRUTAL's `NOTICE.txt`.
- Community health files: contributing guide, security policy, code of conduct (Contributor Covenant 2.1), this changelog, pull request and issue templates, and CODEOWNERS.
- `samples/HelloWindow`: an SDL3 window presenting a Vulkan 1.3 clear-colour swapchain, with resize and minimise handling, validation counting and NativeAOT support.
- `BrutalEngine.Core.Logging`: an instance-owned logging facade (`Logger`, `LogCategory`, `ILogSink`) with a global minimum level and per-category overrides, UTF-8 formatting that allocates nothing for common types (enabled or not), messages truncated to a byte limit, and sink failures isolated and counted. It has no BRUTAL dependency.
- `samples/HelloWindow` on macOS uses the Vulkan loader and MoltenVK bundled next to the executable, so a published build runs with no Vulkan SDK, Homebrew or `VK_DRIVER_FILES`.
- CI runs HelloWindow with validation on Linux, Windows and macOS (JIT and NativeAOT, with negative tests) and uploads the NativeAOT build per platform. Any change under `samples/` gets the full CI tier.
- `BrutalEngine.Core.Logging.Sinks.ConsoleLogSink`: writes each log entry as one UTF-8 line (`2026-11-25T03:04:05.678Z INFO [Category] message`) to standard output, or to standard error from a configurable level (default `Warning`), flushing on errors. Streams can be injected, and steady-state writes allocate nothing.
- `BrutalEngine.Core.Logging.Sinks.MemoryLogSink`: keeps the most recent log entries in fixed, preallocated memory (by entry count and message bytes), overwriting the oldest and counting drops. `CopyTo` copies them into caller buffers, for crash reports and the debug overlay, without allocating.
- `BrutalEngine.Core.Logging.Sinks.RotatingFileLogSink`: writes log lines to `engine.log` in a directory the host provides. Each run starts a fresh file, files rotate by size (default 10 MiB), at most `MaxFiles` are kept (default 5), and entries at or above `FlushLevel` (default `Warning`) are flushed as soon as they're written. It's thread-safe and never throws on I/O failure: it faults permanently, reports the error in `Fault` and counts dropped entries in `DroppedCount`. Steady-state writes allocate nothing.
- `samples/HelloWindow` shows the official BRUTAL logo for at least 2.0 s (the first 120 frames when headless) before the scene, as BRUTAL's licence requires. The logo is drawn unaltered except for scaling, with no shaders: it is composited on the CPU and blitted, or copied at 1:1. A surface that can't show it is a setup failure, never a silent skip. The published output ships BRUTAL's `LICENSE.txt` and `NOTICE.txt`, each BRUTAL package's third-party licences in `LICENSES/`, and a generated `CREDITS.txt` with BRUTAL's and BrutalEngine's copyright lines. CI checks them, plus that the scene ran after the splash.

[Unreleased]: https://github.com/harry-stanley/brutal-engine/compare/v0.1.0-alpha...HEAD
[0.1.0-alpha]: https://github.com/harry-stanley/brutal-engine/releases/tag/v0.1.0-alpha
