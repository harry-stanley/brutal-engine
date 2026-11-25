# Spike S3: headless Vulkan 1.3 on software drivers (Linux + Windows)

Question: can the GitHub-hosted `ubuntu-24.04` and `windows-2025` runners run headless Vulkan 1.3
rendering on a software driver with `VK_LAYER_KHRONOS_validation` on, driven from BRUTAL's Vulkan
bindings in a .NET 10 program that renders offscreen and reads the pixels back?

**Answer: yes, on both.** Lavapipe passes the test with 0 validation errors and every pixel correct, and the injected bug is caught. Setup takes about 30 s per OS
(run https://github.com/harry-stanley/brutal-engine/actions/runs/45549608616).

Not verified: SwiftShader (not tried); whether `VK_DRIVER_FILES` alone works in the elevated Windows runner (HKLM registration was used as well);
real rendering beyond a clear (no pipeline/shaders exercised); behaviour under load or with parallel jobs.

Branch: `spike/s3-software-vulkan` (throwaway). Files: `spikes/s3/VulkanHeadless.cs`, `.github/workflows/spike-s3.yml`.

## CI status

**Both pass on GitHub-hosted runners.** Run [45549608616](https://github.com/harry-stanley/brutal-engine/actions/runs/45549608616) (commit `8cb8c2a`):
- linux (ubuntu-24.04), success: https://github.com/harry-stanley/brutal-engine/actions/runs/45549608616/job/138987356771
- windows (windows-2025), success: https://github.com/harry-stanley/brutal-engine/actions/runs/45549608616/job/138987357080

Re-run on the report commit `0a94885`: [45549932859](https://github.com/harry-stanley/brutal-engine/actions/runs/45549932859). Both jobs succeeded again.

An earlier run, [45548871014](https://github.com/harry-stanley/brutal-engine/actions/runs/45548871014), passed on Linux but ran the test on .NET 11 (see Problems).
Its Windows job died in a diagnostic step before reaching Vulkan: `Get-ChildItem HKLM:\SOFTWARE\Khronos -Recurse` on a missing key
overflowed the pwsh stack after 4 minutes.

The first push was rejected because the gh token lacked the `workflow` scope (it had `gist, read:org, repo`). The maintainer then refreshed the token.

## Test program (`spikes/s3/VulkanHeadless.cs`)

- .NET 10 file-based app with these packages: `Ahwoo.Brutal.Vulkan@2026.10.3` (latest on NuGet), `Ahwoo.Brutal.Vulkan.Abstractions@2026.10.3`,
  `Ahwoo.Brutal.Core@2026.10.4` and `Ahwoo.Brutal.Strings@2026.10.2`. Abstractions is needed only for `MapMemory`, `AllocateCommandBuffer` and `WaitForFence`,
  because the core package's `vkMapMemory`/`vkAllocateCommandBuffers` wrappers are `internal`.
- The instance enables `VK_LAYER_KHRONOS_validation`, `VK_EXT_debug_utils` and `VK_EXT_validation_features` with
  **synchronization validation on**. A debug messenger counts errors and warnings and is also chained into
  instance creation. The program fails if the layer isn't present.
- For every device it prints the name, type, API version, driverID, driverName, driverInfo and conformance version. With `S3_REQUIRE_CPU=1`
  it picks only a `VK_PHYSICAL_DEVICE_TYPE_CPU` device, which proves a software driver ran.
- It creates a device with the Vulkan 1.3 features `dynamicRendering` and `synchronization2`, after checking support with `vkGetPhysicalDeviceFeatures2`.
- The render path is a 64x64 `R8G8B8A8_UNORM` image, a `vkCmdBeginRendering` with `LoadOp=Clear` (0.2, 0.4, 0.8, 1.0),
  `vkCmdPipelineBarrier2` transitions, a `vkCmdCopyImageToBuffer` into a HOST_VISIBLE|HOST_COHERENT buffer (pre-filled with 0xCD),
  a buffer barrier to HOST, `vkQueueSubmit2` and a fence wait (10 s timeout, result checked). Every pixel must equal
  (51, 102, 204, 255).
- Exit codes: 0 pass, 1 pixel mismatch, 2 validation error(s), 3 setup failure/exception. Error VkResults throw `VkResultError`
  in BRUTAL. Non-throwing results (enumerate, fence wait) are checked by hand.
- **Negative test:** `S3_INJECT_ERROR=1` records a barrier with a wrong `oldLayout`. The run must exit 2,
  which proves the layer is really loaded and the messenger is wired up. Without this check, a silently missing layer would look like "0 errors".

## Results

| Environment | Device / driver | API | Validation | Pixels | Negative test |
|---|---|---|---|---|---|
| Local Win11 (26300), NVIDIA RTX 2070 SUPER, driver 621.24, LunarG SDK 1.4.363.0 layers | DiscreteGpu, NvidiaProprietary | 1.4.357 | 0 err / 0 warn | 4096/4096 OK | exit 2 (2 errors: layout mismatch) |
| Local Win11, **lavapipe from pal1000 mesa-dist-win 26.3.1** via `VK_DRIVER_FILES` (non-elevated) | `llvmpipe (LLVM 23.1.6, 256 bits)`, Cpu, MESALlvmpipe, "Mesa 26.3.1 (git-f367ac0da2)", conformance 1.3.1.1 | 1.4.363 | 0 / 0 | 4096/4096 OK | exit 2 |
| Local WSL2 Ubuntu 26.04.1, distro `mesa-vulkan-drivers` 26.0.8 + `vulkan-validationlayers` 1.4.341 (unpacked .deb, `VK_ADD_LAYER_PATH`) | `llvmpipe (LLVM 21.1.8, 256 bits)`, Cpu, MESALlvmpipe, "Mesa 26.0.8-1ubuntu0.3", conformance 1.3.1.1 | 1.4.335 | 0 / 0 | 4096/4096 OK | exit 2 |
| **GitHub ubuntu-24.04** (image 20261116.133.1, Ubuntu 24.04.5), .NET 10.0.15 | `llvmpipe (LLVM 20.1.2, 256 bits)`, Cpu, MESALlvmpipe, "Mesa 25.2.8-0ubuntu0.24.04.5", conformance 1.3.1.1; loader 1.3.275, VVL 1.3.275 | 1.4.318 | **0 / 0** | **4096/4096 OK** | **exit 2** (VUID-VkImageMemoryBarrier2-oldLayout-01211, -01197) |
| **GitHub windows-2025** (image `windows-2025-vs2026` 20261116.315.1, build 26100, elevated), .NET 10.0.15 | `llvmpipe (LLVM 23.1.6, 256 bits)`, Cpu, MESALlvmpipe, "Mesa 26.3.1 (git-f367ac0da2)", conformance 1.3.1.1; loader 1.4.363.1, VVL 1.4.363 | 1.4.363 | **0 / 0** | **4096/4096 OK** | **exit 2** (2 layout errors) |

Local timings: the Vulkan work itself takes 0.27–0.75 s per run (instance + validation ≈ 0.1–0.5 s, submit + fence ≈ 2–3 ms).

## Setup (as implemented in `spike-s3.yml`)

Shared: `actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1` (v7.0.1),
`actions/setup-dotnet@0f4a354dd5547a28c70b7853dbd95d5b67886e73` (v6.1.0) with `dotnet-version: 10.0.x`, and `S3_REQUIRE_CPU=1`.

### Linux (ubuntu-24.04)
- `sudo apt-get install --no-install-recommends libvulkan1 mesa-vulkan-drivers vulkan-validationlayers vulkan-tools`.
  Noble versions per Launchpad (2026-11-25): `mesa` **25.2.8-0ubuntu0.24.04.5** (noble-updates, main), `vulkan-validationlayers`
  **1.3.275.0-1** (universe), `vulkan-loader`/`libvulkan1` **1.3.275.0-1build1**. Apt packages are pinned by the
  Ubuntu archive's signatures rather than SHA-256. Pin `=version` if exact reproducibility matters.
- `VK_DRIVER_FILES=$(ls /usr/share/vulkan/icd.d/lvp_icd*.json)`. **The ICD file name varies**: Ubuntu's Mesa packages (24.04's
  25.2.8 and 26.04's 26.0.8) ship `lvp_icd.json`, while mesa-dist-win and older builds use `lvp_icd.x86_64.json`. The workflow globs for it. **Observed:**
  the runner image already had `libvulkan1` 1.3.275.0-1build1 but no ICDs or layers; apt installed Mesa 25.2.8-0ubuntu0.24.04.5 and VVL 1.3.275.0-1.
- .NET: `spikes/s3/global.json` pins SDK `10.0.100` with `rollForward: latestFeature`, which resolved to 10.0.404 on both runners.
- BRUTAL loads `libvulkan.so.1` (`ImportResolver.cs`), which `libvulkan1` provides.

### Windows (windows-2025)
- **Loader location matters:** BRUTAL calls `NativeLibrary.Load("vulkan-1", asm, DllImportSearchPath.System32)`, so
  `vulkan-1.dll` must be in `C:\Windows\System32`. Shipping it next to the app does **not** work. **Observed on the runner:** the image
  already has `System32\vulkan-1.dll` **1.3.300.0**, and the SDK silent install upgraded it to **1.4.363.1** (so the workflow's fallback copy
  never ran). No Khronos registry keys exist before setup (no ICDs, no layers).
- Lavapipe comes from `https://github.com/pal1000/mesa-dist-win/releases/download/26.3.1/mesa3d-26.3.1-release-msvc.7z`,
  SHA-256 `64e84cf13d67dd354640bf443ed0445e6a83afd06a5729f0750b3744d633b3f1` (66,270,373 bytes; matches GitHub's asset
  digest and was re-hashed locally). It extracts `x64\vulkan_lvp.dll` (56 MB, no other DLL dependencies needed locally) and
  `x64\lvp_icd.x86_64.json` (`library_path: .\vulkan_lvp.dll`, api 1.4.363).
- The driver is registered as a DWORD 0 value under `HKLM\SOFTWARE\Khronos\Vulkan\Drivers`. `VK_DRIVER_FILES` is also set, but the
  loader ignores driver/layer path env vars in **elevated** processes, and the runner is elevated (`elevated: True` was logged). Because both
  were set, the run doesn't show which mechanism the loader used. Local non-elevated runs show that `VK_DRIVER_FILES` alone works. Registry registration is the safe choice.
- Loader and validation layers come from the LunarG Vulkan SDK **1.4.363.1**:
  `https://sdk.lunarg.com/sdk/download/1.4.363.1/windows/vulkansdk-windows-X64-1.4.363.1.exe`,
  SHA-256 `c02932bd9a968b22d404536108adaacac908c081cd6b0113f5bfefc523dd0a6a` (303,066,832 bytes, hashed locally).
  It installs silently with `--root C:\VulkanSDK\1.4.363.1 --accept-licenses --default-answer --confirm-command install` (exit 0). **Observed:** the
  installer itself registers `C:\VulkanSDK\1.4.363.1\Bin\VkLayer_khronos_validation.json` in `HKLM\SOFTWARE\Khronos\Vulkan\ExplicitLayers`,
  so the workflow's fallback wasn't needed.
- Alternatives I looked at and rejected for pinning: Khronos `Vulkan-ValidationLayers` publishes Windows binaries only in the mutable
  `nightly` release (the tagged `vulkan-sdk-*` releases are Android-only), and `Vulkan-Loader` releases are Android-only.
  LunarG's standalone VulkanRT URLs I guessed returned 404. SwiftShader was not tried, because lavapipe worked.

## Setup time cost
Measured from the step timestamps of run 45549608616:

| | Linux | Windows |
|---|---|---|
| Vulkan setup | apt **30 s** (20 s in the earlier run) | Mesa download+verify+extract **3 s**, SDK download **9 s** + silent install **20 s** = **32 s** |
| Build the file-based app (restore + compile, cold NuGet) | 13–16 s | 27–30 s |
| Test run + negative test | < 1 s each (Vulkan work ≈ 150 ms) | ≈ 1 s each (≈ 170–300 ms) |
| Whole job, wall clock | 62 s (44 s earlier) | 75 s |

## Problems found
1. The gh token lacked the `workflow` scope, so the first push of a workflow file was rejected. The maintainer fixed it with `gh auth refresh -s workflow`.
2. **The runner images preinstall the .NET 11 SDK (11.0.100).** `setup-dotnet` with `10.0.x` installs 10, but the CLI still picks 11, so the first Linux
   run built and ran on **.NET 11.0.0**. Fixed with `global.json`, and both runners then reported .NET 10.0.15. ci.yml needs a repo-root `global.json`.
3. `Get-ChildItem HKLM:\SOFTWARE\Khronos -Recurse` on a non-existent key makes pwsh walk all of HKLM and die with a stack overflow after
   about 4 minutes. Read specific keys with `Test-Path` + `Get-Item` instead.
4. BRUTAL's Windows loader lookup is **System32 only**, so a CI job (or an end-user machine without GPU drivers) must install `vulkan-1.dll` system-wide.
5. The lavapipe ICD JSON name differs between Mesa packagings (`lvp_icd.json` vs `lvp_icd.x86_64.json`).
6. Core `Brutal.Vulkan` keeps `MapMemory` and `AllocateCommandBuffers` internal, so even a minimal test needs `Brutal.Vulkan.Abstractions`.
7. Ubuntu 24.04's validation layers are old (1.3.275, Jan 2024) compared with the Windows SDK's 1.4.363. Validation coverage differs between the two OSes.
   For parity, use the LunarG Linux SDK tarball or build the layers.

## Recommendation for ci.yml GPU test jobs
**Go: put software-Vulkan GPU tests in ci.yml on both OSes.** They cost about 30 s of setup per job, and validation errors fail the build.
- **Linux job** (`ubuntu-24.04`): `apt-get install --no-install-recommends libvulkan1 mesa-vulkan-drivers vulkan-validationlayers`
  (drop `vulkan-tools` unless you want `vulkaninfo`), then `VK_DRIVER_FILES=$(ls /usr/share/vulkan/icd.d/lvp_icd*.json)`.
  If layer parity with Windows matters, use the LunarG Linux SDK tarball 1.4.363.1 instead of the old 1.3.275 distro layers (not tried here).
- **Windows job** (`windows-2025`): pinned + SHA-256-checked pal1000 mesa-dist-win `release-msvc.7z` (extract only `vulkan_lvp.dll` +
  `lvp_icd.x86_64.json`), registered in `HKLM\SOFTWARE\Khronos\Vulkan\Drivers`, plus the pinned + SHA-256-checked LunarG SDK silent install
  (which registers the layer in HKLM and upgrades System32 `vulkan-1.dll`). Add `actions/cache` for the two downloads, keyed on version+hash, if
  the 9 s SDK download ever becomes flaky. Make the step idempotent and don't recurse registry keys in pwsh.
- **Pin the SDK with `global.json`** (`10.0.100`, `rollForward: latestFeature`). The images preinstall .NET 11 SDK 11.0.100, and without it
  `setup-dotnet 10.0.x` doesn't stop the CLI from building/running on .NET 11.
- Run `S3_REQUIRE_CPU`-style device selection, so a runner that gains a real GPU/ICD can't change results silently, and always run the
  **negative injected-error test**, so a missing layer can't pass as "0 errors".
- Treat validation warnings as reported but not failing for now (0 today). Consider failing on them once the engine renderer exists.
- Keep `VK_EXT_validation_features` sync validation on. It costs nothing measurable at this scale.
