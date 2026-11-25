# ADR-0002: Abstract render interface, Vulkan 1.3 first, Slang shaders

- **Status:** Accepted; amended by [ADR-0015](0015-tested-platforms-windows-linux.md) (macOS natives are kept, but not tested in CI)
- **Date:** 2026-11-25
- **Deciders:** Harry Stanley (maintainer)

## Context

The engine must render on Windows, Linux and macOS. BRUTAL provides Vulkan 1.3 bindings plus VMA, and OpenGL bindings. macOS has no native Vulkan, and MoltenVK has offered Vulkan 1.3 since v1.3.0 (April 2025). BRUTAL does not bundle MoltenVK or the loader on macOS. We may later want native Metal or D3D12 backends, or SDL_GPU (exposed by BRUTAL's SDL3 bindings), so engine code above the graphics layer must not depend on Vulkan.

## Options considered

1. **Render interface + Vulkan 1.3 first:** one modern explicit API everywhere, room for more backends. More upfront design work.
2. **Vulkan only, no abstraction:** least code now, but locks every subsystem to Vulkan.
3. **OpenGL first:** widest compatibility, but deprecated on macOS and a dead end for modern features.
4. **Vulkan + OpenGL fallback:** doubles testing for a shrinking set of old GPUs.

## Decision

A WebGPU-level render interface (`BrutalEngine.Graphics`) with a Vulkan 1.3 backend (dynamic rendering, synchronization2, VMA) as the only v1 backend. MoltenVK and the Vulkan loader are bundled on macOS. Shaders are written in Slang and compiled offline to SPIR-V by the cooker. Every backend must pass one shared conformance suite.

## Consequences

- Minimum GPU requirement is Vulkan 1.3, as stated in [Vision and scope](../01-vision-and-scope.md).
- We own the macOS native build (`natives.yml`) and its tests (risk R1).
- Spike S4 must confirm Slang works through `Ahwoo.Brutal.Slang` on all three OSes. If it doesn't, we fall back to ShaderC/GLSL and record that in a superseding ADR.
