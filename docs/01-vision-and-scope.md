# 1. Vision and scope

## What BrutalEngine is

BrutalEngine is a public, open-source 2D game engine for C# developers. It is distributed as NuGet packages. A game is an ordinary .NET application that references the engine libraries. The engine sits on top of the BRUTAL framework, which provides interop, memory, collections, maths, concurrency, logging, localisation and native bindings (Vulkan, SDL3, Box2D, FreeType, ImGui and others). BRUTAL deliberately has no renderer, scene model or editor. BrutalEngine provides them.

## Goals

1. **Production grade.** Stable, fully tested and deployable in shipped commercial games. No hacks, shortcuts or placeholder features presented as finished.
2. **Cross-platform from day one.** Windows and Linux are the tested targets: a feature is not done until it passes CI on both. macOS is best-effort: its code paths are kept, but nothing builds or tests them in CI until a Mac is available ([ADR-0015](adr/0015-tested-platforms-windows-linux.md)).
3. **Stable public API.** Semantic versioning, tracked public surface, documented deprecation paths. Breaking changes only in major versions after 1.0.
4. **Performance by design.** Zero managed allocations per frame in steady state. Data-oriented internals. Measurable budgets enforced by tests.
5. **Code-first.** Everything the future editor does goes through the same public runtime API. The editor is a client of the engine, not a fork.
6. **Shippable output.** One command turns a game project into a signed, self-contained build for each platform (NativeAOT by default).

## Non-goals (for v1)

- 3D rendering. The render interface must not block it later, but no 3D features are planned.
- Mobile, web and consoles. The platform layer stays portable (no Windows/Linux/macOS assumptions above it), but no work is scheduled.
- A visual editor. It comes after v1, on the same runtime. A debug overlay (ImGui) arrives early and is not the editor.
- Visual scripting or an embedded scripting language. Gameplay code is C#.
- Hot reload of C# code. The architecture keeps it possible (see [Architecture, hot-reload readiness](03-architecture.md#321-hot-reload-readiness)), but it is not a v1 feature. Asset hot reload *is* a v1 feature.

## Quality bar

These apply to every PR, not just releases:

- Builds with zero warnings, with warnings treated as errors and the full analyzer set enabled.
- Every feature has automated tests at the right layer (see [Testing strategy](05-testing-strategy.md)). Bug fixes start with a failing regression test.
- Every native call's return or status code is checked. No silent failure.
- No managed allocations in per-frame engine paths. Enforced by allocation tests.
- No unmanaged leaks. Debug builds track every native allocation and GPU object, and tests fail on a leak at shutdown.
- Vulkan validation layers run in all GPU tests. Any validation error fails the test.
- Every public API has XML documentation and appears in the tracked public API file.

## Supported platforms (v1)

| OS | Architecture | .NET RID | Graphics path | Notes |
|----|--------------|----------|---------------|-------|
| Windows 10 22H2+ / Windows 11 | x64 | `win-x64` | Vulkan 1.3 (driver-provided loader) | |
| Linux (glibc), X11 and Wayland | x64 | `linux-x64` | Vulkan 1.3 (system `libvulkan.so.1`) | Minimum glibc to be confirmed in spike S1 |
| macOS on Apple silicon | arm64 | `osx-arm64` | Vulkan 1.3 via bundled MoltenVK and Vulkan loader | Best-effort and unverified: not built or tested in CI ([ADR-0015](adr/0015-tested-platforms-windows-linux.md)). Minimum macOS version to be confirmed in spike S1 |

These are the only three RIDs that BRUTAL ships native libraries for. Adding Intel macOS, ARM Linux or ARM Windows would mean building and maintaining every native dependency ourselves. That is out of scope for v1 and needs its own ADR.

BRUTAL itself lists only Windows and Linux as supported. macOS support is BrutalEngine's own responsibility: we bundle MoltenVK and the loader ourselves (see [Risks R1](10-risks.md)). They ran on GitHub's macOS runner in M0, but from M1 on macOS isn't tested in CI and stays unverified until a Mac is available ([ADR-0015](adr/0015-tested-platforms-windows-linux.md)).

**Minimum GPU:** any Vulkan 1.3 device, with `dynamicRendering` and `synchronization2`. That covers roughly NVIDIA GTX 900 and later, AMD GCN 4 and later, Intel Gen9 and later on current drivers, and, unverified, all Apple silicon via MoltenVK 1.3 or later.

## Game styles the defaults favour

Both pixel art and HD 2D are first-class:

- Pixel art: integer scaling, pixel-snapped cameras, a virtual-resolution render target, nearest-neighbour sampling, and no sub-pixel jitter.
- HD 2D: linear filtering with mipmaps, MSAA or analytic anti-aliasing for shapes, SDF text, normal-mapped lighting.

Large-scale simulation (tens of thousands of active entities) is a design pressure on the world model and the renderer, not a separate mode.

## v1 feature scope

All four groups are in v1, delivered in the milestone order in the [Roadmap](09-roadmap.md):

1. Sprites and batching, cameras, input, audio, the asset pipeline, text, tilemaps.
2. 2D physics (Box2D v3), sprite-sheet and skeletal animation.
3. In-game UI (separate from ImGui) and localisation.
4. 2D lighting and post-processing, particles, networking.

This is the scope of a full commercial engine. It is achievable because every milestone ships a usable, released, tested subset. There is never a long period with nothing working.

## Licensing obligations

- The engine is MIT licensed ([ADR-0008](adr/0008-licence.md)).
- BRUTAL is under the BRUTAL License v1.0. Any product that *uses BRUTAL*, which includes every game made with BrutalEngine and the future editor, must:
  1. include the BRUTAL copyright notice in its credits screen (or equivalent),
  2. show the unaltered BRUTAL logo before gameplay or before the software's primary function, legibly and no less prominently than any other logo,
  3. distribute BRUTAL's `NOTICE.txt` alongside the product.
- The engine makes compliance the default. A built-in, skippable-after-legible splash shows the BRUTAL logo. A credits API includes the notice. The publish step copies `NOTICE.txt` and all third-party licence files into the output. Removing any of these is the game developer's choice and their legal responsibility, and the docs say so plainly.
- **Name.** "BrutalEngine" uses the BRUTAL name, which belongs to Ahwoo Limited. The BRUTAL License grants copyright permissions, not trademark rights. Before the first public package release, confirm with Ahwoo that the name and the `BrutalEngine.*` NuGet IDs are acceptable (see [Risks R9](10-risks.md)).
