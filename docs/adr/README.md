# Architecture decision records

One file per decision that is expensive to reverse. See the [ADR process](../08-workflow.md#adr-process).

| ADR | Title | Status |
|-----|-------|--------|
| [0001](0001-dotnet-10-lts.md) | Target .NET 10 LTS | Accepted |
| [0002](0002-render-interface-vulkan-first.md) | Abstract render interface, Vulkan 1.3 first, Slang shaders | Accepted |
| [0003](0003-sdl3-platform.md) | SDL3 for platform, input and audio device | Accepted |
| [0004](0004-audio.md) | Own mixer on SDL3 audio, FMOD as an optional later plugin | Accepted |
| [0005](0005-offline-asset-cooking.md) | Offline asset cooking with dev-mode hot reload | Accepted |
| [0006](0006-world-model.md) | World model: archetype ECS with hierarchy | Accepted |
| [0007](0007-nativeaot-release.md) | NativeAOT for release builds, JIT for development | Accepted |
| [0008](0008-licence.md) | MIT licence for the engine | Accepted |
| [0009](0009-github-actions-ci.md) | GitHub as source of truth, GitHub Actions CI | Accepted |
| [0010](0010-monorepo-lockstep-packages.md) | Monorepo with lockstep-versioned NuGet packages | Accepted |
| [0011](0011-brutal-objectpool-dependency.md) | Accept BRUTAL's .NET 11 `Microsoft.Extensions.ObjectPool` dependency, pinned to GA | Accepted |
| [0012](0012-m1-shader-cooking.md) | M1 shaders: a Slang-only cook command and committed SPIR-V | Accepted |
