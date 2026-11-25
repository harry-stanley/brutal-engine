# M0 spikes (throwaway code)

Code and raw reports from the M0 spikes that ran locally on the maintainer's Windows machine (RTX 2070 SUPER, Ryzen 9 5950X). This code is **not** engine code. It isn't built by CI and will never be merged into `main`. The summarised results are in `docs/spikes/` on `main`.

| Folder | Spike |
|--------|-------|
| `s1-s2-windows/` | S1 SDL3 window + Vulkan 1.3 swapchain, and S2 NativeAOT publish (Windows) |
| `s4-slang/` | S4 Slang → SPIR-V + reflection, ShaderC comparison |
| `s5-ecs/` | S5 archetype ECS prototype and benchmarks |
| `s6-s8-research/` | S6 audio decoding and S8 text shaping research (package inspection evidence) |

The GitHub-runner spikes live on their own branches: `spike/s3-software-vulkan` (Linux and Windows lavapipe) and `spike/s1-macos-moltenvk` (macOS MoltenVK).
