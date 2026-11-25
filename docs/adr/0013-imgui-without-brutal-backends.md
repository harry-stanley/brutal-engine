# ADR-0013: ImGui overlay on `Ahwoo.Brutal.ImGui` only, with our own backends

- **Status:** Accepted
- **Date:** 2026-11-26
- **Deciders:** the maintainer

## Context

M1 ships `BrutalEngine.DebugOverlay`: ImGui with our SDL3 platform backend and a renderer on our render interface, plus frame-timing and log panels ([Roadmap, M1](../09-roadmap.md#m1-core-runtime-and-renderer-02), docs/09-roadmap.md:43). It sits at L4 as a host, so it may reference backends (`tests/BrutalEngine.Architecture.Tests/EngineLayers.cs:32-40`).

[ADR-0003](0003-sdl3-platform.md) says "We implement BRUTAL's `IPlatformBackend` for SDL3 for the ImGui debug overlay and the future editor" (docs/adr/0003-sdl3-platform.md:18), and the [tech stack](../02-tech-stack.md#brutal-packages-used) listed `Ahwoo.Brutal.ImGui.Backends` for that. Reading the package changes the picture (paths are in the BRUTAL source tree; nuspecs are from `~/.nuget/packages/<id>/<version>/`; all checked 2026-11-26):

- **The interfaces carry nothing.** `IPlatformBackend` and `IRendererBackend` are each `IDisposable` plus one method, `void NewFrame()` (`Brutal.ImGui.Backends/Interfaces/IPlatformBackend.cs:3-6`, `Brutal.ImGui.Backends/Interfaces/IRendererBackend.cs:3-6`). Implementing them gives no shared behaviour or tooling.
- **The package brings GLFW and OpenGL.** `Ahwoo.Brutal.ImGui.Backends` 2026.10.3 depends on `Ahwoo.Brutal.ImGui` 2026.10.3, `Ahwoo.Brutal.Glfw` 2026.10.5, `Ahwoo.Brutal.OpenGl` 2026.10.2, `Ahwoo.Brutal.Vulkan.Abstractions` 2026.10.3 and `CommunityToolkit.HighPerformance` 8.4.2 (`ahwoo.brutal.imgui.backends.nuspec`). `Glfw` and `OpenGl` in turn depend on `Ahwoo.Brutal.Glfw.Native` and `Ahwoo.Brutal.OpenGl.Native`, with natives for all three RIDs. The engine uses neither GLFW nor OpenGL ([Tech stack](../02-tech-stack.md#brutal-packages-used), "Not used").
- **Its Vulkan renderer can't sit on our render interface.** `VulkanBackend.CreateInfo` requires a BRUTAL `DeviceEx`, a `Queue` and a `VkRenderPass` (`Brutal.ImGui.Backends/RendererBackends/VulkanBackend.cs:23-33`), and the port skips imgui_impl_vulkan's dynamic-rendering path (`VulkanBackend.cs:16`). Our Vulkan backend uses dynamic rendering ([ADR-0002](0002-render-interface-vulkan-first.md)), and no engine module above L2 may see Vulkan types.
- **There is no SDL backend** in the package: its only platform backend is `PlatformBackends/GlfwBackend.cs`.
- **The core binding is enough on its own.** `Ahwoo.Brutal.ImGui` 2026.10.3 binds Dear ImGui 1.92.2 (`Brutal.ImGui/Generated/Macros.gen.cs:9`) and depends on `Ahwoo.Brutal.ImGui.Native`, `Core`, `Localization`, `Memory`, `Numerics`, `Strings` and `CommunityToolkit.HighPerformance` (`ahwoo.brutal.imgui.nuspec`), none of which is GLFW or OpenGL. Through `Strings` it also brings the R16 package that [ADR-0011](0011-brutal-objectpool-dependency.md) deals with.

## Options considered

1. **DebugOverlay references only `Ahwoo.Brutal.ImGui`** (1.92.2, `Generated/Macros.gen.cs:9`); our SDL3 platform backend and render-interface renderer are our own types, not implementations of BRUTAL's interfaces. ADR-0013 supersedes that one sentence of ADR-0003 (ADR-0003 gets only a status/link update). Pros: no GLFW or OpenGL natives, no dead dependency, same functionality. Cons: both backends are our code to write and test, which ADR-0003 already expected for the platform side.
2. **Reference `Ahwoo.Brutal.ImGui.Backends` to implement the two interfaces**, as ADR-0003 says. Cons: ships GLFW and OpenGL natives in every overlay-enabled build for two one-method interfaces.
3. **Defer the overlay to M2.** Cons: drops a roadmap line from M1, and the overlay is where M1's frame timings and log panel are shown.

## Decision

Decision (maintainer, 2026-11-26): option 1. `BrutalEngine.DebugOverlay` references `Ahwoo.Brutal.ImGui` and no other ImGui package. Its SDL3 platform backend and its renderer on `BrutalEngine.Graphics` are engine types that don't implement BRUTAL's `IPlatformBackend` or `IRendererBackend`. This supersedes the sentence "We implement BRUTAL's `IPlatformBackend` for SDL3 for the ImGui debug overlay and the future editor" in ADR-0003; the rest of ADR-0003 stands.

## Consequences

- **Packages:** `Ahwoo.Brutal.ImGui.Backends`, `Ahwoo.Brutal.Glfw` and `Ahwoo.Brutal.OpenGl` are never referenced by engine modules, so GLFW and OpenGL natives never reach a game. A package that pulls them in again needs a new ADR.
- **Platform backend:** our code feeds ImGui's IO (display size and scale, mouse, keyboard, text input, clipboard, cursors) from the engine's platform layer and is tested headless. Whether it reads Platform's backend-neutral events or `Platform.Sdl3` directly is the DebugOverlay design's choice; the layer map allows both for a host.
- **Renderer:** it draws ImGui's draw lists only through `BrutalEngine.Graphics` (buffers, textures, a pipeline and bind groups), so it works on any future backend and goes through the graphics conformance suite like every other pipeline. Its shaders follow [ADR-0012](0012-m1-shader-cooking.md).
- **Future editor:** it uses the same two backends; nothing in this ADR is overlay-only.
- **BRUTAL's backends as reference only:** the GLFW, OpenGL and Vulkan backends in `Brutal.ImGui.Backends` may be read to understand ImGui's backend contract, but they aren't dependencies.
- **Revisit if** BRUTAL publishes an ImGui backend package that has an SDL3 platform backend or a renderer on an API we can sit on, without GLFW and OpenGL dependencies.
