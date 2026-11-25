# ADR-0003: SDL3 for platform, input and audio device

- **Status:** Accepted; the ImGui backend sentence is superseded by [ADR-0013](0013-imgui-without-brutal-backends.md)
- **Date:** 2026-11-25
- **Deciders:** Harry Stanley (maintainer)

## Context

We need windows, events, keyboard/mouse/gamepad/touch input with IME, display info, an audio device and a Vulkan surface on three OSes. BRUTAL binds both GLFW and SDL3 (`Ahwoo.Brutal.Sdl`, SDL 3.4, with natives for win-x64, linux-x64 and osx-arm64, verified 2026-11-25). The bindings include `SDL_Vulkan_CreateSurface`, `SDL_OpenGamepad` and `SDL_OpenAudioDeviceStream`. BRUTAL's ImGui backends support GLFW only.

## Options considered

1. **SDL3:** broadest device support (gamepad database, haptics, IME, touch, pen), includes audio. We have to write an ImGui platform backend.
2. **GLFW:** reuses BRUTAL's ImGui backend. Weaker input, no audio, so a second dependency is needed for audio.

## Decision

SDL3, behind our own `BrutalEngine.Platform` abstraction (backend `BrutalEngine.Platform.Sdl3`). We implement BRUTAL's `IPlatformBackend` for SDL3 for the ImGui debug overlay and the future editor.

## Consequences

- No SDL types appear in engine public APIs above the backend.
- The SDL3 ImGui platform backend is our code to maintain and test.
- SDL's offscreen/dummy drivers give us headless testing.
