# Spike S1/S3 (macOS): bundled Vulkan loader + MoltenVK on GitHub-hosted macos-15

Status: **done, definitive for offscreen rendering**. Throwaway branch `spike/s1-macos-moltenvk`.
Everything below comes from real runs unless it is marked *unverified*.

## TL;DR

- **Metal is available** on `macos-15` arm64 runners. The device is `Apple Paravirtual device`, a
  Virtualization.framework GPU, not a bare M-series GPU. Metal executes work: a Swift clear + readback passed.
- **MoltenVK 1.4.2** (official release asset) runs on it. It reports "MoltenVK version 1.4.2, supporting
  Vulkan version 1.4.357" and conformance 1.4.4.0. The device reports `apiVersion 1.3.357`, because the
  app asked for 1.3. `dynamicRendering` and `synchronization2` are supported, and `VK_KHR_portability_subset`
  is exposed and enabled.
- **A bundled loader (built by us) + bundled MoltenVK works through BRUTAL's bindings, with no SDK or
  Homebrew installed.** The test instance → device → 64x64 RGBA8 clear via dynamic rendering → copy to
  buffer → readback passes. All 4096/4096 pixels match (255,128,64,255), with 0 debug-messenger errors and
  0 warnings.
- **Validation layers work.** We built `VK_LAYER_KHRONOS_validation` from source and loaded it from a
  bundled folder via `VK_ADD_LAYER_PATH`. A deliberate canary error was reported, and the real workload was
  clean (0 errors, 0 warnings).
- **Catch: BRUTAL's resolver does NOT find `libvulkan.1.dylib` "beside the app" in a portable
  (non-RID) build.** It works in these cases:
  - in a RID-specific build/publish (`-r osx-arm64`)
  - when the dylib is under `runtimes/osx-arm64/native/`
  - when it is preloaded by absolute path
  - via `DYLD_LIBRARY_PATH`
- **The bundled loader does not find the bundled MoltenVK on its own.** You must point it at the ICD JSON
  (`VK_DRIVER_FILES` / `VK_ADD_DRIVER_FILES`, or a native `setenv` from the app before `vkCreateInstance`).
  Without that you get `Found no drivers!` → `VK_ERROR_INCOMPATIBLE_DRIVER`.

## Runs

| run | commit | result | notes |
|---|---|---|---|
| https://github.com/harry-stanley/brutal-engine/actions/runs/45548991815 | 0d2b7cc | failure | Found the resolver issue: every app-dir scenario failed with `DllNotFoundException`, and only the `DYLD_LIBRARY_PATH` one rendered. Validation build: 397 s. |
| https://github.com/harry-stanley/brutal-engine/actions/runs/45549852747 | 104976f | **success** | All mandatory scenarios passed, including validation. The app ran on the .NET 11.0.0 runtime because file-based apps default to the SDK's TFM. |
| https://github.com/harry-stanley/brutal-engine/actions/runs/45550233317 | 674c1fa | **success** | Same as above with `TargetFramework=net10.0`: both jobs passed on **.NET 10.0.15** (smoke 59 s, validation 13 min 55 s incl. a 767 s VVL build). This is the reference run. |

## Environment (from the runs)

- Runner image: `macos15 20261118.0550.1`, macOS **15.8.2 (24H232)**, arm64, `hw.model VirtualMac2,1`,
  3 CPUs, 7 GiB RAM.
- Xcode 16.4 (16F6), Swift 6.1.2. The preinstalled .NET SDK is 11.0.100. `setup-dotnet` adds 10.0.x, and
  the app is pinned to `net10.0`.
- System Vulkan: none. There is no `libvulkan*` in `/opt/homebrew/lib` or `/usr/local/lib`, no
  MoltenVK/vulkan Homebrew formulae, and `VULKAN_SDK` is empty. The workflow asserts this before running.

## (a) Metal

`swift spikes/smac/metal_probe.swift` (takes about 5 s, mostly Swift JIT):

```
MTLCopyAllDevices: 1 device(s)
name: Apple Paravirtual device
isLowPower: false isHeadless: false isRemovable: false
hasUnifiedMemory: true
recommendedMaxWorkingSetSize: 4778 MiB
supportsFamily(apple7): false  apple8: false  apple9: false  mac2: true  metal3: false
argumentBuffersSupport tier: 1
Metal clear readback pixel0: 0,255,0,255  -> Metal GPU clear: OK
```

MoltenVK's own view of the device: GPU Family Apple 5, Mac 1, Mac 2; Read-Write Texture Tier 2; Metal
Shading Language 3.2; 4778 MB; vendor 0x106b, device 0xf080205. It uses Metal argument buffers for
descriptor sets and MTLEvent for semaphores.

**The GPU is paravirtualised.** It lacks the Apple7+/Metal3 families, and argument buffers are only tier 1.
So feature coverage and performance are **not representative of real Apple-silicon Macs**. It is fine for
functional and correctness tests.

## (b) Bundled loader + MoltenVK through BRUTAL

### How BRUTAL resolves the loader (checked against source and runtime)

`Brutal.Vulkan/ImportResolver.cs` runs these calls in order and takes the first that succeeds:

```
NativeLibrary.TryLoad(candidate, typeof(Vulkan).Assembly, DllImportSearchPath.LegacyBehavior, out dll)
candidates: "libvulkan.1.dylib", "/opt/homebrew/lib/libvulkan.1.dylib", "/usr/local/lib/libvulkan.1.dylib", "VulkanEx"
```

For a bare name, the .NET runtime first probes each entry of `NATIVE_DLL_SEARCH_DIRECTORIES`. It then
probes the calling assembly's directory, but **only if the search-path flags include `AssemblyDirectory`**.
`LegacyBehavior` (0) does not include it. After that it calls a plain `dlopen(name)`, which uses dyld
search, i.e. `DYLD_LIBRARY_PATH`. These rules are from the "default probing" docs, and the runs bear them
out:

- **Portable build** (`dotnet build file.cs`): `NATIVE_DLL_SEARCH_DIRECTORIES =
  <app>/runtimes/osx-arm64/native/:<dotnet>/shared/Microsoft.NETCore.App/<ver>/`. The app dir is not in it,
  so `<app>/libvulkan.1.dylib` is **not found** (`DllNotFoundException`).
- **RID-specific publish** (`dotnet publish -r osx-arm64 --self-contained false`): NuGet natives are
  flattened, and `NATIVE_DLL_SEARCH_DIRECTORIES = <app>/:<dotnet>/shared/...`, so the copy beside the app
  **is found**.
- The `VulkanEx` fallback can't act as a loader. `libVulkanEx.dylib` exports no `vk*` symbols (`nm -gU`) and
  links `@rpath/libvulkan.1.dylib` itself. With no loader present the resolver fails with
  `DllNotFoundException`.

### Scenario matrix

Latest run, .NET 10.0.15. Every run's cwd was `$RUNNER_TEMP`, not the app dir.

| scenario | layout / env | result |
|---|---|---|
| rid-appdir | RID publish, `libvulkan.1.dylib` + `libMoltenVK.dylib` + `MoltenVK_icd.json` beside app, `VK_DRIVER_FILES=<app>/MoltenVK_icd.json` | **PASS** (loader from app dir; `vkCmdBeginRendering` resolves into `<app>/libMoltenVK.dylib`; 4096/4096 px) |
| portable-appdir | portable build, loader beside app | FAIL as expected: `DllNotFoundException` |
| portable-runtimesnative | portable build, loader in `runtimes/osx-arm64/native/` | **PASS** |
| portable-preload | portable build, `NativeLibrary.Load("<abs>/libvulkan.1.dylib")` before first `Vulkan.*` use | **PASS**: BRUTAL's bare-name `dlopen` binds to the already-loaded image |
| portable-frameworks-dyld | loader + MoltenVK in `Frameworks/`, `DYLD_LIBRARY_PATH=Frameworks` | PASS. The loader warns that the MoltenVK path differs from the OS-loaded path when the JSON and DYLD dir disagree (run 1) |
| rid-noloader | RID publish without libvulkan | FAIL as expected: `DllNotFoundException` (VulkanEx fallback useless) |
| rid-selfsetenv | no env, app calls libc `setenv("VK_DRIVER_FILES", <app>/MoltenVK_icd.json)` before the loader is touched | **PASS** |
| rid-adddriverfiles | `VK_ADD_DRIVER_FILES` | **PASS** |
| rid-nodriverenv | no driver env | FAIL as expected: `vkCreateInstance: Found no drivers!` → `VK_ERROR_INCOMPATIBLE_DRIVER`; instance extensions drop to 4 |
| rid-loaderdebug | `VK_LOADER_DEBUG=error,warn,info,driver` | PASS. The log shows that only `<app>/MoltenVK_icd.json` was searched/found, and that `./libMoltenVK.dylib` is resolved relative to the JSON |
| rid-validation | + `VK_ADD_LAYER_PATH=<layers>` + `--require-validation` | **PASS**. Layer `VK_LAYER_KHRONOS_validation spec 1.4.363` was found. The canary `vkCreateBuffer(usage=0)` was reported (`VUID-VkBufferCreateInfo-None-09500`), and the real workload had 0 errors and 0 warnings |

Test app details (`spikes/smac/VkSmoke.cs`, a .NET 10 file-based app using Ahwoo.Brutal.Vulkan 2026.10.3):

- **Instance:** portability enumeration, plus a `VK_EXT_debug_utils` messenger (also chained into
  `vkCreateInstance`) that counts errors and warnings.
- **Device and info:** prints each device's name, type, apiVersion, `VkPhysicalDeviceDriverProperties` and
  `Vulkan13Features`. The device has `VK_KHR_portability_subset` (if exposed) and 1.3
  dynamicRendering + synchronization2.
- **Rendering:** clears a 64x64 RGBA8 image through `vkCmdBeginRendering` with sync2 barriers, then calls
  `vkCmdCopyImageToBuffer` and `vkQueueSubmit2`.
- **Checks:** reads back from host-visible memory and verifies every pixel (±1 rounding).
- **Error handling:** BRUTAL throws `VkResultError` on error codes. Raw calls (`vkMapMemory`,
  `vkWaitForFences`) are checked explicitly. The app exits nonzero on any failure.
- **Load proof:** `dladdr` reports which binary the loader and the device functions were loaded from.
- It also passes on Windows (RTX 2070 SUPER, NVIDIA 621.24) with and without validation.

## Exact pinned steps (`spikes/smac/ci/*.sh`, `.github/workflows/spike-macos.yml`)

1. **MoltenVK** `v1.4.2` (release commit `db66022459ffb663aa2b50f6b018bc2e124f5edf`):
   - Download `https://github.com/KhronosGroup/MoltenVK/releases/download/v1.4.2/MoltenVK-macos.tar`.
     The asset's sha256 `f95765a6229cb7b915990a2890ce12ebe36a730b021545d3d52ae69ce4c4024e` matches the
     GitHub release digest and is verified.
   - Use `MoltenVK/dynamic/dylib/macOS/libMoltenVK.dylib`: 10 MB universal (x86_64+arm64), ad-hoc signed,
     install name `@rpath/libMoltenVK.dylib`. Bundled sha256 `aef00b13…4a4c5f`.
   - Use its `MoltenVK_icd.json` (`api_version 1.4.0`, `is_portability_driver: true`) with `library_path`
     rewritten to `./libMoltenVK.dylib`.
2. **Vulkan-Loader** `vulkan-sdk-1.4.363.0` (commit `fa218055ff4948b159f31d43f800c808a13d51df`, asserted):
   - Run `git clone --depth 1 --branch …`, then
     `cmake -DCMAKE_BUILD_TYPE=Release -DUPDATE_DEPS=ON -DBUILD_TESTS=OFF -DCMAKE_OSX_ARCHITECTURES=arm64`.
     `UPDATE_DEPS` pulls Vulkan-Headers at the tag's `known_good.json` pin.
   - Then `cmake --build`. Copy `libvulkan.1.dylib` with `cp -L`, because it is a symlink to the versioned
     dylib.
   - The result is 716 KB, arm64, ad-hoc signed, install name `@rpath/libvulkan.1.dylib`, sha256
     `79cabec1…3793a`, and reports loader version 1.4.363.
3. **Vulkan-ValidationLayers** `vulkan-sdk-1.4.363.1` (commit `317cff060893783f96d4174f50491e6374eb3714`,
   asserted):
   - Same cmake flags plus `-DBUILD_WERROR=OFF`.
   - Produces `libVkLayer_khronos_validation.dylib` (31 MB) plus a manifest with `library_path` rewritten to
     `./libVkLayer_khronos_validation.dylib`.
   - GitHub has no macOS VVL binaries (only Android/Linux/Windows nightly), and the LunarG SDK was
     deliberately not used.
4. **App**: `dotnet publish spikes/smac/VkSmoke.cs -c Release -r osx-arm64 --self-contained false -o app`,
   then copy the dylibs and the ICD JSON in.

## Costs (wall clock on the runner)

| item | time |
|---|---|
| smoke job total (checkout → Metal probe → natives → 10 scenarios → upload) | **~1 min** (57–59 s) |
| MoltenVK download + verify + extract (57 MB tar) | 1 s |
| Vulkan-Loader clone + configure + build | 17–24 s |
| dotnet publish + build of the test app (incl. NuGet restore) | ~7 s |
| each scenario run | <1–1 s (device creation ~0.9–1.1 s on first MoltenVK init) |
| Swift Metal probe | 5–6 s |
| **VVL build** | **397 s, 731 s, 767 s** in three runs (≈6.5–13 min; 3 vCPUs, high variance, slower when two macOS jobs ran at once); validation job total 7–14 min |

The repo is public, so standard GitHub-hosted macOS runners cost no minutes. The run annotations warned
that "jobs targeting macOS arm64 runners may experience longer queue times". Our jobs started within
seconds.

## Problems found / gotchas

1. **The portable build doesn't search the app directory** (see above). This trap affects any BrutalEngine
   game that ships with `dotnet build`/portable layouts. Use a RID-specific publish, put the dylib in
   `runtimes/osx-arm64/native/` (via a native NuGet package), or preload it.
2. **Bundled MoltenVK isn't discovered automatically.** The loader only looks in system and user locations.
   For `.app` bundles it also looks in `Contents/Resources/vulkan/icd.d` (*unverified here: no .app bundle
   was built*). The engine must set `VK_DRIVER_FILES` natively with libc `setenv`. This worked. The note
   that `Environment.SetEnvironmentVariable` does not reach native `getenv` on Unix is from .NET's
   behaviour and was not tested here. `VK_LUNARG_direct_driver_loading` is exposed by the loader and is a
   cleaner alternative (*untested*).
3. **The SIP gotcha:** `DYLD_*` variables are stripped when they pass through a protected binary such as
   `/usr/bin/env`. The scripts export them in the shell instead. The `DYLD_LIBRARY_PATH` route is therefore
   fragile and not recommended for shipping.
4. **File-based apps default to the SDK's TFM:** the runner's SDK 11 produced a net11 app. Pin
   `#:property TargetFramework=net10.0`. A `global.json` next to the `.cs` is ignored when dotnet is run
   from another cwd.
5. `actions/upload-artifact` rejects paths containing `..`. Keep the work dir under the workspace.
6. `xcodebuild -version | head -1` crashes xcodebuild with SIGPIPE (cosmetic).

## Not verified (honest gaps)

- **Windowed presentation:** no `CAMetalLayer`/`VK_EXT_metal_surface` swapchain or GLFW/SDL window. The
  extension is exposed. Whether a runner session can create and present a window is untested.
- Shaders/pipelines (SPIR-V → MSL translation by SPIRV-Cross inside MoltenVK) and compute. Only
  clear/copy was exercised. These are next to add.
- Real Apple GPU families (Apple7–9, Metal 3, tier-2 argument buffers) and performance.
- `.app` bundle layout, code signing with Developer ID, hardened runtime, notarisation and Gatekeeper
  quarantine. Everything here is ad-hoc signed and was never quarantined.
- osx-x64 (Intel): not tested. MoltenVK is universal, but BRUTAL ships no osx-x64 natives.
- `macos-14`/`macos-26` images and `-privateapi` MoltenVK.

## Recommendations

### natives.yml (macOS job)

- **Runner and inputs:** run on `macos-15`. Pin MoltenVK by release tag and **sha256 of the release asset**,
  which GitHub exposes as `assets[].digest`. Pin the loader and VVL by `vulkan-sdk-*` tag **and** commit
  SHA, and assert both.
- **Loader:** build from source every time (≈20 s). Thin it with `lipo` if we ever go universal.
- **MoltenVK:** use the release dylib. Optionally `lipo -thin arm64` it (10 MB → ~5 MB) for osx-arm64-only
  packages.
- **VVL:** build from source (6.5–12 min) and **cache it** with `actions/cache` keyed on the VVL tag + image
  OS + Xcode version. Ship it only in dev/debug tooling packages, not in game builds (31 MB).
- **Packaging:** ship `libvulkan.1.dylib`, `libMoltenVK.dylib` and `MoltenVK_icd.json` (relative
  `library_path`) as **NuGet native assets under `runtimes/osx-arm64/native/`**. That directory is always in
  `NATIVE_DLL_SEARCH_DIRECTORIES` (portable builds), and RID publishes flatten it into the app dir, which is
  also searched. Both layouts passed.
- **Engine startup (macOS):**
  - Before first `Vulkan.*` use, if the user has not set `VK_DRIVER_FILES`/`VK_ICD_FILENAMES`, call libc
    `setenv("VK_DRIVER_FILES", <dir of libMoltenVK>/MoltenVK_icd.json)`.
  - Optionally also preload the loader by absolute path with `NativeLibrary.Load`, so that
    resolution doesn't depend on the build layout.
  - Keep `VK_ADD_LAYER_PATH` as the dev switch for validation.
  - Longer term, consider proposing upstream that BRUTAL resolve the macOS loader with `AssemblyDirectory`
    (fixing its own TODO "Package the Vulkan loader on macOS").
- **Licences:** add the MoltenVK, Vulkan-Loader and VVL licences (Apache-2.0) to NOTICE when shipping.

### macOS CI GPU testing

- **Do it on every PR.** A ~1 min `macos-15` job can run real Vulkan-on-Metal tests through BRUTAL:
  offscreen render, readback and pixel asserts, using the bundled natives from the natives build (or an
  artifact).
- Run it with validation layers from the cached VVL build, failing on any messenger error. Keep a canary
  test so a silently missing layer can't pass.
- Treat the paravirtual GPU as a **functional/correctness** target only: no perf numbers, no
  feature-tier assumptions. Golden-image tests should use tolerances, because a paravirtual GPU and real
  Apple GPUs may differ in rounding. For example, 0.5 → 128 here versus 127 on NVIDIA.
- Next spike: add a shader pipeline (SPIR-V → MSL) and a windowed swapchain attempt to find the ceiling.

### Q6: is a real Mac still needed?

**Not for development CI. Yes, before shipping a macOS release.** The runners settle that Vulkan-on-Metal
works with bundled natives through BRUTAL, including validation. A real Mac is still required for these,
none of which a runner can show:

- real Apple GPU families and performance
- windowing, HiDPI and input feel
- the `.app` bundle with Developer ID signing, notarisation and Gatekeeper
- an actual player's experience

A cheap option is a borrowed or rented Apple-silicon Mac (cloud Mac or tester) for each release candidate,
rather than owning one for day-to-day work.
