# Spike S1+S2 (Windows): SDL3 + Vulkan 1.3 clear loop on BRUTAL, JIT and NativeAOT

Date: 2026-11-25. Machine: Windows 11 Pro 10.0.26300, NVIDIA RTX 2070 SUPER (driver reports Vulkan 1.4.357),
Vulkan SDK 1.4.363.0, .NET SDK 10.0.404 (pinned via global.json), ILCompiler 10.0.15, VS 18 C++ tools for the link.

**Result: both spikes pass.**
- **S1 (JIT):** exit 0. 300 frames presented, 3 swapchain recreations, 0 validation errors and 0 warnings.
- **S2 (NativeAOT win-x64):** 0 warnings and 0 errors. The binary ran with output identical to the JIT run.

## Files (all in this directory)
| File | Purpose |
|---|---|
| `Program.cs` | The spike (584 lines) |
| `Spike.csproj` | net10.0, `AllowUnsafeBlocks`, `IsAotCompatible=true`, `TrimmerSingleWarn=false`, `InvariantGlobalization=true` |
| `global.json` | Pins SDK 10.0.100 with rollForward latestFeature (resolved 10.0.404) |
| `jit-run.log`, `jit-leak.log` | JIT runs: normal, and the negative validation test |
| `aot-publish.log`, `aot-publish-verbose.log` | AOT publish output (normal and `-v:n`) |
| `aot-run.log`, `aot-leak.log` | AOT runs: normal, and the negative validation test |
| `publish-aot/` | AOT output |

I used a csproj instead of a file-based app so the AOT switches and the analyzer settings are explicit.

## Package versions (resolved from obj/project.assets.json)
Direct references:
- Ahwoo.Brutal.Sdl **2026.10.3**
- Ahwoo.Brutal.Vulkan **2026.10.3**
- Ahwoo.Brutal.Vulkan.Abstractions **2026.10.3** (needed, see gap G1)

Transitive packages:
- Ahwoo.BRUTAL.Sdl.Native 2026.10.3
- Ahwoo.BRUTAL.Vulkan.Native 2026.10.3
- Core 2026.10.4
- Interop 2026.10.2
- Logging 2026.10.3
- Memory 2026.10.2
- Numerics 2026.10.2
- Strings 2026.10.2
- Collections 2026.10.2
- CommunityToolkit.HighPerformance 8.4.2
- Microsoft.Extensions.Logging, Logging.Console, Configuration, Configuration.Binder, DI and Options: all 10.0.0
- **Microsoft.Extensions.ObjectPool 11.0.0-preview.5.26302.115**: a preview package, pulled in by Ahwoo.BRUTAL.Strings 2026.10.2

These were the latest Sdl and Vulkan versions on nuget.org at the time (index.json lists 2026.10.2 and 2026.10.3).

## What the program does (all verified by running it)
1. **SDL setup.**
   - `Sdl.Init.Initialize(SdlInitFlags.Video)`.
   - `Sdl.Video.CreateWindow(..., Vulkan | Resizable | HighPixelDensity)`.
   - `_ = Vulkan.GetInstanceProcAddr` first, so BRUTAL loads `vulkan-1` from System32.
2. **Instance.**
   - Extensions: `Sdl.Vulkan.GetInstanceExtensions()` returned `VK_KHR_surface` and `VK_KHR_win32_surface`, plus `VK_EXT_debug_utils` and `VK_EXT_validation_features`.
   - Layer: `VK_LAYER_KHRONOS_validation`. Its presence is checked first with `Vulkan.EnumerateInstanceLayerProperties_Count/_Write`.
   - `VkValidationFeaturesEXT` enables **synchronization validation**, so the frame sync is checked by the layer and not just basic API usage.
   - The debug-messenger create info is also chained into instance creation, so vkCreate/DestroyInstance are covered.
3. **Debug messenger.**
   - `instance.CreateDebugUtilsMessengerEXT(...)` with an `[UnmanagedCallersOnly(CallConvs=[typeof(CallConvCdecl)])]` callback passed as `new PfnVkDebugUtilsMessengerCallbackEXT(&DebugCallback)`.
   - The callback counts errors and warnings. The process exits 1 if there were any errors.
4. **Surface:** `Sdl.Vulkan.CreateSurface(window, instance.VkHandle)`, wrapped as `new VkSurfaceKHR(nint)`.
5. **GPU choice.**
   - Requirements: API version ≥ 1.3, `VK_KHR_swapchain`, and `dynamicRendering` plus `synchronization2` (queried through `GetFeatures2` with `VkPhysicalDeviceVulkan13Features` chained on).
   - It also needs a graphics queue family where `GetSurfaceSupportKHR` is true. Discrete GPUs are preferred.
6. **Device:** `PhysicalDevice.CreateDevice(new Device.CreateInfo { Extensions, QueueCreateInfos, Next = &enable13 })`, with dynamicRendering and synchronization2 enabled.
7. **Swapchain.**
   - Format: B8G8R8A8_SRGB / SRGB_NONLINEAR. Present mode: FIFO. Image count: minImageCount+1. Composite alpha: opaque if supported.
   - `CurrentExtent == 0xFFFFFFFF` is handled. A zero extent (minimised window) is skipped.
   - `OldSwapchain` is passed on recreate.
8. **Frame loop: 2 frames in flight.**
   - Per frame in flight: a command buffer, an acquire semaphore and a fence.
   - Per swapchain image: a render-finished semaphore.
   - Order: wait fence → acquire. The fence is reset only after a successful acquire, so an OUT_OF_DATE acquire can't deadlock. Then reset fence → record → `Submit2` → present.
   - Recording: `PipelineBarrier2` (UNDEFINED→COLOR_ATTACHMENT, stage chained with the acquire semaphore's wait stage), then `BeginRendering` clear to an animated colour, `EndRendering`, and `PipelineBarrier2` (COLOR_ATTACHMENT→PRESENT_SRC, chained with the signal stage).
   - Swapchain problems: OUT_OF_DATE from acquire or present, and SUBOPTIMAL, set a recreate flag. Recreation does `device.WaitIdle()`, creates the new swapchain from the old one, then destroys the old one.
9. **Unattended run.**
   - Auto-exits after N presented frames (default 300).
   - Calls `Sdl.Video.SetWindowSize` at 1/3 of the run (1024x720) and at 2/3 (640x480), so the resize and recreate path is exercised.
   - `--no-resize` disables that. `--leak` is the negative test.
10. **Teardown in reverse creation order.**
    1. `WaitIdle`.
    2. Swapchain semaphores, views and swapchain.
    3. Fences and semaphores.
    4. Command pool.
    5. Device (`using`).
    6. Surface.
    7. Messenger.
    8. Instance (`using`).
    9. Window (`using`).
    10. `Sdl.Init.Quit`.

Every VkResult is checked. Wrappers that throw `VkResultError` are left to propagate. Wrappers that return a VkResult (`WaitForFences`, `GetSwapchainImagesKHR_Write`, `GetSurfaceFormatsKHR_Write`, `EnumerateInstanceLayerProperties_Write`, Acquire, Present) are compared explicitly. SDL bool returns (`SetWindowSize`, `GetWindowSizeInPixels`) are checked, and `SdlException` propagates from Init, CreateWindow and CreateSurface.

## S1: JIT run (`bin/Release/net10.0/Spike.exe 300`)
```
Spike S1+S2: frames=300 resizeTest=True aot=False
SDL instance extensions: VK_KHR_surface, VK_KHR_win32_surface
GPU: NVIDIA GeForce RTX 2070 SUPER (DiscreteGpu), API 1.4.357, queue family 0
Swapchain: 800x600 B8G8R8A8SRGB/SRGBNonlinearKHR images=3 present=FIFO
  recreated swapchain at frame 0: 800x600, images=3
  recreated swapchain at frame 100: 1024x720, images=3
  recreated swapchain at frame 200: 640x480, images=3
Rendered 302 frames, presented 300 in 4.90s (61.3 fps); recreations=3 outOfDate=2 suboptimal=0
Teardown: device objects destroyed
Validation messages: errors=0 warnings=0
Exit code 0
```
- **Exit code 0.** The 302 rendered vs 300 presented difference is the 2 presents that returned OUT_OF_DATE after the resizes.
- **Validation is really active.** `Spike.exe 60 --no-resize --leak` skips destroying one semaphore. The layer then reported `VUID-vkDestroyDevice-device-05137 ... 1 leaked objects ... VkSemaphore`, counted as errors=1, and the process exited 1. This proves the layer, the messenger, the callback thunk and the exit-code path all work.
- **Unneeded recreate at frame 0.** SDL sends a WindowResized or PixelSizeChanged event right after the window is created. The extent didn't change, so the engine should compare extents before recreating.

## S2: NativeAOT (`dotnet publish -c Release -r win-x64 -p:PublishAot=true -o publish-aot`)
- **Publish exit 0: `0 Warning(s)`, `0 Error(s)`.** `grep IL2xxx/IL3xxx` matches only csc's default `/nowarn:IL2121` line.
  - I deleted obj/.../win-x64 before publishing, so ILC ran fresh ("Generating native code").
  - `TrimmerSingleWarn=false`, and ilc.rsp has no `--singlewarn`, so warnings in library assemblies are not collapsed into one-per-assembly. There were simply none.
- **Output: 15 MB in total, but most of it is the PDB.**
  | File | Size |
  |---|---|
  | `Spike.exe` | 1,973,760 B (~1.9 MB) |
  | `SDL3.dll` | 2,698,752 B |
  | `VulkanEx.dll` | 11,264 B (unused on Windows, see G7) |
  | `Spike.pdb` | 10.2 MB |

  Shippable size is about 4.7 MB. The JIT build, for comparison, ships 24 managed DLLs, including the Microsoft.Extensions.* stack.
- **The AOT run (`aot-run.log`) is identical to the JIT run**, apart from `aot=True`: 300 presented, 3 recreations, 2 out-of-date, errors=0, warnings=0, exit 0, 4.90 s.
- **The AOT leak test (`aot-leak.log`) behaved the same as under JIT:** errors=1, exit 1. So `[UnmanagedCallersOnly]` callbacks through `PfnVkDebugUtilsMessengerCallbackEXT` work under AOT.
- **The BRUTAL DLLs don't declare `IsTrimmable` or `IsAotCompatible`.** I grepped the assembly bytes for the attribute strings in Sdl, Vulkan, Core, Interop, Logging and Vulkan.Abstractions, and none had them. The "0 warnings" result therefore covers only the code this spike reaches. Other BRUTAL APIs, such as the logging and configuration paths, could still produce warnings once used.

## BRUTAL API problems and binding gaps
| # | Finding | Where (BRUTAL checkout) |
|---|---|---|
| G1 | **You can't allocate command buffers with core Brutal.Vulkan alone.** `VkCommandBufferAllocateInfo.CommandBufferCount` is `internal`, and `AllocateCommandBuffersInternal` is `internal`. The only public path is `DeviceExtensions.AllocateCommandBuffer(s)` in **Vulkan.Abstractions**, so I had to add that package. | Brutal.Vulkan/Generated/Structs/VkCommandBufferAllocateInfo.gen.cs:52; Generated/Extensions/VkDeviceExtensions.gen.cs:2006; Brutal.Vulkan.Abstractions/Extensions/DeviceExtensions.cs:346,362 |
| G2 | **`AcquireNextImageKHR` and `Queue.PresentKHR` throw `VkResultError` on `ErrorOutOfDateKHR`.** A routine resize condition becomes an exception. SuboptimalKHR is returned, not thrown. The raw `device.PInvoke.vkAcquireNextImageKHR.Invoke(...)` table entry is public and returns the raw VkResult. | VkDeviceExtensions.gen.cs:8326,8361 (acquire), 8417,8442 (PresentKHRInternal); Manual/Classes/Queue.cs:89; Generated/Functions.gen.cs:4534 |
| G3 | **`uint64_t` is bound as signed `nint`.** This affects timeouts (`AcquireNextImageKHR`, `WaitForFences`) and `VkSemaphoreSubmitInfo.Value` (timeline values). `-1` works as UINT64_MAX on 64-bit, but the types are signed and platform-sized. | VkDeviceExtensions.gen.cs:8329,1173,6146; Generated/Structs/VkSemaphoreSubmitInfo.gen.cs:50 |
| G4 | **The single-fence `WaitForFence` that DemoVulkan uses exists only in Vulkan.Abstractions.** Core only has `WaitForFences(span, waitAll, timeout)`. | Brutal.Vulkan.Abstractions/Extensions/DeviceExtensions.cs:632 |
| G5 | **The SDL Vulkan binding is minimal and untyped.** It is hand-bound with only three functions (GetInstanceExtensions, CreateSurface, DestroySurface), with Vulkan handles passed as `nint` and the allocator hard-coded to 0. There are no bindings for `SDL_Vulkan_GetPresentationSupport`, `SDL_Vulkan_LoadLibrary`, `SDL_Vulkan_GetVkGetInstanceProcAddr` or `SDL_Vulkan_UnloadLibrary`, and no typed bridge to `Instance`/`VkSurfaceKHR`. `GetInstanceExtensions` allocates `string[]` (fine at init). | Brutal.Sdl/Manual/Libraries/Sdl.Vulkan.cs:7-71 |
| G6 | **`Instance.CreateInfo` has no defaults.** `EnabledExtensions` and `EnabledLayers` are non-defaulted `HashSet`s, so leaving either out gives an NRE in the ctor (`.Count` at :28-29). `Device.CreateInfo` does default them (`= []`). The two are inconsistent. | Brutal.Vulkan/Manual/Classes/Instance.cs:10-18,28-29; Device.cs:8-14 |
| G7 | **`VulkanEx.dll` ships in win-x64 output but is never loaded on Windows.** ImportResolver loads `vulkan-1` from System32 on Windows and uses VulkanEx only as a macOS fallback. It is a dead 11 KB file. | Brutal.Vulkan/ImportResolver.cs:43-44,56 |
| G8 | **A preview package sits in the production dependency graph.** Ahwoo.BRUTAL.Strings 2026.10.2 depends on Microsoft.Extensions.ObjectPool **11.0.0-preview.5**. | obj/project.assets.json |
| G9 | **Heavy transitive dependencies.** Core, Logging, Memory, Numerics and Strings all pull Microsoft.Extensions.Logging.Console, which brings Configuration, Binder and DI. AOT trims them (no warnings), but a JIT build ships 24 DLLs. | nuspec dependency lists |
| G10 | **`Queue` serialises Submit, Submit2 and PresentKHR under a per-queue `Lock`.** This is not a bug, but it is a hidden cost and policy in the hot path. `PresentKHRInternal` and `Submit2Internal` are internal, so the only lock-free route is the raw function table. | Manual/Classes/Queue.cs:73-98 |

Things that worked well and need no workaround:
- sType is preset in every struct used.
- `void* Next` chaining works for Features2, Vulkan13Features, ValidationFeatures and DebugUtilsMessengerCreateInfo.
- `PipelineBarrier2`, `BeginRendering`/`EndRendering`, `Queue.Submit2` and the span overloads `ResetFences([f])` / `WaitForFences([f], …)` work.
- The `_Count`/`_Write` enumeration helpers work.
- `VkClearColorValue` converts implicitly to `VkClearValue`.
- The function-pointer callback types are AOT-safe.

## Recommendations
**Platform.Sdl3**
- Own a small typed Vulkan bridge over `Sdl.PInvoke.Vulkan_*`: return `VkSurfaceKHR`, take `Instance`.
- Add the missing `SDL_Vulkan_GetPresentationSupport` binding yourself with your own `[LibraryImport("SDL3")]`, so queue-family selection doesn't need a surface. Do the same for `SDL_Vulkan_LoadLibrary` if you want SDL and BRUTAL to share one loader explicitly. Today both load `vulkan-1`, which happens to be the same DLL on Windows.
- Treat WindowPixelSizeChanged as a hint only. Use `GetWindowSizeInPixels` together with `surfaceCaps.currentExtent`, and recreate only when the extent actually changes. This avoids the frame-0 recreate seen here.
- Handle the zero-extent (minimised) state by not rendering. Prefer `WaitEvent` over sleeping.

**Graphics.Vulkan**
- Use raw `device.PInvoke.vkAcquireNextImageKHR/vkQueuePresentKHR.Invoke` (or a thin wrapper of your own) in the frame loop, so OUT_OF_DATE is a return code, not an exception (G2). Keep the throwing wrappers for init.
- Depend on Brutal.Vulkan.Abstractions, or vendor its `AllocateCommandBuffers`, because of G1. Better still, file upstream to make `CommandBufferCount` public.
- Keep this spike's sync model as the baseline:
  - N frames in flight with fence + acquire semaphore + command buffer each, and render-finished semaphores per swapchain image.
  - Reset the fence only after a successful acquire.
  - Use sync2 barriers whose stages chain with the semaphore wait and signal stages.
- Add `VK_EXT_swapchain_maintenance1` (present fences) when available. Recreating after `WaitIdle` and destroying the per-image present semaphores passed validation, but the spec doesn't strictly guarantee that the presentation engine has finished with those semaphores.
- Keep synchronization validation enabled in debug builds, and keep a `--leak`-style self-test in CI so "0 errors" is known to mean something.
- Wrap `uint64` timeouts and timeline values in your own `ulong` API so the signed `nint` bindings don't leak into engine code (G3).
- AOT: keep `IsAotCompatible` and `TrimmerSingleWarn=false` on, and publish AOT in CI. BRUTAL doesn't mark itself trimmable, so new BRUTAL API surface you touch can bring in warnings.

## What was NOT verified
- **Interactive use.** Manual resizing by dragging, minimise/restore, and closing the window were not tested by hand. Resizing was exercised only through `SDL_SetWindowSize`. I didn't look at the window, so the colours were not checked visually, and no RenderDoc capture was taken.
- **Display variety.** Only the NVIDIA RTX 2070 SUPER and a single monitor were tested. HighPixelDensity scaling and multi-GPU selection were not tested.
- **The SUBOPTIMAL path.** It never triggered (suboptimal=0), so the code that handles it ran zero times.
- **Validation coverage.** Only validation core plus sync validation were enabled. Best-practices and GPU-assisted validation were not.
- **Other platforms and setups.** Linux and macOS were not tested, nor were AOT builds that touch other BRUTAL packages.
- **Long-running behaviour.** Long-run leaks and performance were not measured. Each run lasted about 5 s at roughly 61 fps (vsync).
