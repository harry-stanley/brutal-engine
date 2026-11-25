# Spike S3: headless Vulkan 1.3 on software drivers (Linux + Windows)

Question: can the GitHub-hosted `ubuntu-24.04` and `windows-2025` runners run headless Vulkan 1.3
rendering on a software driver with `VK_LAYER_KHRONOS_validation` on, driven from BRUTAL's Vulkan
bindings in a .NET 10 program that renders offscreen and reads the pixels back?

Branch: `spike/s3-software-vulkan` (throwaway). Files: `spikes/s3/VulkanHeadless.cs`, `.github/workflows/spike-s3.yml`.

## CI status

<!--CI-STATUS-->
**Not run on GitHub yet.** The push of the spike branch was rejected:
`refusing to allow an OAuth App to create or update workflow .github/workflows/spike-s3.yml without workflow scope`.
The gh token's scopes are `gist, read:org, repo`, and a push that adds a file under `.github/workflows/`
needs the `workflow` scope too. Fix: `gh auth refresh -h github.com -s workflow`, which is interactive and needs
the maintainer. Then push:
`git -C <worktree> -c credential.helper= -c 'credential.helper=!gh auth git-credential' push -u https://github.com/harry-stanley/brutal-engine.git spike/s3-software-vulkan`.
The branch is committed locally and ready to push.
<!--/CI-STATUS-->

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
<!--CI-ROWS-->
| GitHub ubuntu-24.04 | not run (push blocked) | | | | |
| GitHub windows-2025 | not run (push blocked) | | | | |
<!--/CI-ROWS-->

Local timings: the Vulkan work itself takes 0.27–0.75 s per run (instance + validation ≈ 0.1–0.5 s, submit + fence ≈ 2–3 ms).

## Setup (as implemented in `spike-s3.yml`)

Shared: `actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1` (v7.0.1),
`actions/setup-dotnet@0f4a354dd5547a28c70b7853dbd95d5b67886e73` (v6.1.0) with `dotnet-version: 10.0.x`, and `S3_REQUIRE_CPU=1`.

### Linux (ubuntu-24.04)
- `sudo apt-get install --no-install-recommends libvulkan1 mesa-vulkan-drivers vulkan-validationlayers vulkan-tools`.
  Noble versions per Launchpad (2026-11-25): `mesa` **25.2.8-0ubuntu0.24.04.5** (noble-updates, main), `vulkan-validationlayers`
  **1.3.275.0-1** (universe), `vulkan-loader`/`libvulkan1` **1.3.275.0-1build1**. Apt packages are pinned by the
  Ubuntu archive's signatures rather than SHA-256. Pin `=version` if exact reproducibility matters.
- `VK_DRIVER_FILES=$(ls /usr/share/vulkan/icd.d/lvp_icd*.json)`. **The ICD file name varies**: Ubuntu 26.04's Mesa
  ships `lvp_icd.json`, while older builds ship `lvp_icd.x86_64.json`. The workflow globs for it.
- BRUTAL loads `libvulkan.so.1` (`ImportResolver.cs`), which `libvulkan1` provides.

### Windows (windows-2025)
- **Loader location matters:** BRUTAL calls `NativeLibrary.Load("vulkan-1", asm, DllImportSearchPath.System32)`, so
  `vulkan-1.dll` must be in `C:\Windows\System32`. Shipping it next to the app does **not** work. The workflow copies the
  SDK's `Bin\vulkan-1.dll` into System32 if it's missing.
- Lavapipe comes from `https://github.com/pal1000/mesa-dist-win/releases/download/26.3.1/mesa3d-26.3.1-release-msvc.7z`,
  SHA-256 `64e84cf13d67dd354640bf443ed0445e6a83afd06a5729f0750b3744d633b3f1` (66,270,373 bytes; matches GitHub's asset
  digest and was re-hashed locally). It extracts `x64\vulkan_lvp.dll` (56 MB, no other DLL dependencies needed locally) and
  `x64\lvp_icd.x86_64.json` (`library_path: .\vulkan_lvp.dll`, api 1.4.363).
- The driver is registered as a DWORD 0 value under `HKLM\SOFTWARE\Khronos\Vulkan\Drivers`. `VK_DRIVER_FILES` is also set, but the
  loader ignores driver/layer path env vars in **elevated** processes, and hosted Windows runners run elevated. This is
  based on loader behaviour I know of and is **unverified on the runner**; the workflow prints the elevation state.
- Loader and validation layers come from the LunarG Vulkan SDK **1.4.363.1**:
  `https://sdk.lunarg.com/sdk/download/1.4.363.1/windows/vulkansdk-windows-X64-1.4.363.1.exe`,
  SHA-256 `c02932bd9a968b22d404536108adaacac908c081cd6b0113f5bfefc523dd0a6a` (303,066,832 bytes, hashed locally).
  It installs silently with `--root C:\VulkanSDK\1.4.363.1 --accept-licenses --default-answer --confirm-command install`, and the workflow
  registers `Bin\VkLayer_khronos_validation.json` in `HKLM\...\ExplicitLayers` if the installer didn't.
- Alternatives I looked at and rejected for pinning: Khronos `Vulkan-ValidationLayers` publishes Windows binaries only in the mutable
  `nightly` release (the tagged `vulkan-sdk-*` releases are Android-only), and `Vulkan-Loader` releases are Android-only.
  LunarG's standalone VulkanRT URLs I guessed returned 404. SwiftShader was not tried, because lavapipe worked.

## Setup time cost
<!--TIMING-->
Not measured on runners yet (push blocked). Locally the SDK download took 3.3 s on a fast link. The runner cost to expect is
apt ≈ 20–60 s on Linux, and on Windows a 303 MB SDK download plus silent install plus a 66 MB Mesa download. **Estimates, unverified.**
<!--/TIMING-->

## Problems found
1. **The gh token lacks the `workflow` scope**, so no workflow file can be pushed from this machine until `gh auth refresh -s workflow`.
2. BRUTAL's Windows loader lookup is **System32 only**, so a CI job (or an end-user machine without GPU drivers) must install `vulkan-1.dll` system-wide.
3. The lavapipe ICD JSON name differs between Mesa packagings (`lvp_icd.json` vs `lvp_icd.x86_64.json`).
4. Core `Brutal.Vulkan` keeps `MapMemory` and `AllocateCommandBuffers` internal, so even a minimal test needs `Brutal.Vulkan.Abstractions`.
5. Ubuntu 24.04's validation layers are old (1.3.275, Jan 2024) compared with the Windows SDK's 1.4.363. Validation coverage differs between the two OSes.
   For parity, use the LunarG Linux SDK tarball or build the layers.

## Recommendation for ci.yml GPU test jobs
<!--RECOMMENDATION-->
Provisional, until the runner results are in:
- Linux: add a `gpu-tests-linux` job on `ubuntu-24.04` with the apt packages above plus the globbed `VK_DRIVER_FILES`. It's cheap, and the
  software path is proven locally on Mesa 26.0.8.
- Windows: use the same pattern with the pinned LunarG SDK and mesa-dist-win lavapipe, both SHA-256-checked, plus HKLM driver registration.
  Cache the extracted SDK `Bin` and `vulkan_lvp.dll` with `actions/cache` keyed on version+hash, to skip the 303 MB download.
  Strip the install down to `vulkan-1.dll` + the `VkLayer_khronos_validation` dll/json once the runner run confirms which files are needed.
- Always run the negative (injected-error) test alongside the GPU tests so that a missing layer can't pass silently.
- Pin `S3_REQUIRE_CPU`-style device selection in tests so that a runner which gains a GPU/ICD doesn't change the results.
<!--/RECOMMENDATION-->
