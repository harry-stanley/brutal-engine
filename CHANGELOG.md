# Changelog

All notable changes to BrutalEngine are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until 1.0.0, versions are `0.y.z` and breaking changes can happen between minor releases. They are always listed under "Breaking", with migration notes. See [CI/CD and release: Versioning](docs/06-ci-cd-and-release.md#versioning).

Every pull request adds its user-visible changes to the `Unreleased` section.

## [Unreleased]

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

[Unreleased]: https://github.com/harry-stanley/brutal-engine/commits/main
