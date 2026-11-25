# Design: Vulkan backend (`BrutalEngine.Graphics.Vulkan`)

## Purpose

`BrutalEngine.Graphics.Vulkan` is the only v1 backend of the render interface ([ADR-0002](../adr/0002-render-interface-vulkan-first.md), [Architecture 3.8](../03-architecture.md#38-graphics-render-interface-brutalenginegraphics-and-vulkan-backend)). The [render interface note](graphics.md) owns the public API and the backend contract (`BrutalEngine.Graphics.Backend`: `IGraphicsBackend`, `IBackendDevice`, the frame plan, the failure channels, surface and swapchain lifetime, device-loss rules). This note implements that contract in Vulkan 1.3 terms through BRUTAL's bindings and changes nothing in it. The [platform note](platform.md#4-vulkan-surface-source) owns the window's surface source; the backend sees it only as the Graphics-owned `WindowSurfaceSource`, so Graphics.Vulkan never references Platform (`tests/BrutalEngine.Architecture.Tests/LayerMap.cs:108-115`).

Every section says what M0 code it ports (`samples/HelloWindow`, `tests/BrutalEngine.Aot.Smoke`) and which issue of the split (section 17) builds it. The samples' sync is proven, but their error handling is sample-grade (exceptions for statuses, process-wide counters); nothing is copied without the rules below.

Rules that shape every section:

- **Statuses are values.** Every result-returning Vulkan or VMA call goes through one wrapper (section 13) and becomes a `Result` or a `SwapchainStatus`, as the contract requires. BRUTAL's convenience wrappers throw on error codes, including `VK_ERROR_DEVICE_LOST` (`Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs:8356-8365`), so the frame path never calls them.
- **No static mutable state** ([architecture tests](architecture-tests.md#rules)): the validation counters, the loader decision, the fault injector and every pool belong to an instance. The only statics are `const` values, pure functions and the `[UnmanagedCallersOnly]` callback, a function, not a field.
- **Owner thread only.** The contract calls every `IBackendDevice` member on the device's owner thread, never re-entrantly. So the backend takes no locks and calls the queue through the raw function table (section 13).
- **Windows and Linux are the tested targets.** Decision (maintainer, 2026-11-26): macOS is best-effort and unverified in M1, and leaves CI. The macOS paths (portability enumeration, the bundled loader and MoltenVK) are still designed and ported, and are marked "unverified on macOS" wherever they appear.

## The backend's public surface

The host (Runtime, a test, a sample) creates the backend before anything else touches Vulkan, then passes it to `GraphicsDevice.Create` ([graphics note section 2](graphics.md#2-device-creation-adapters-and-capabilities)). Nothing else in the assembly is public, and no public signature names a binding type.

```csharp
namespace BrutalEngine.Graphics.Vulkan;

public sealed class VulkanBackend : IGraphicsBackend
{
    public static Result<VulkanBackend> Create(VulkanBackendOptions options); // main thread, before the platform starts
    public string Name { get; }                                // "Vulkan"
    public string? LoaderPath { get; }                         // the preloaded loader's absolute path; null = system loader
    public Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(BackendDeviceRequest request);
    public Result<IBackendDevice> CreateDevice(BackendDeviceRequest request, int adapterIndex, BackendObjectTable objects);
}

public sealed record VulkanBackendOptions
{
    public required string AppDirectory { get; init; }         // fully qualified; where bundled natives would be
    public required Logger Logger { get; init; }               // category "Graphics.Vulkan": loader decisions
    public string? LoaderPath { get; init; }                   // an explicit loader (--vulkan-loader); fully qualified
    public ReadOnlyMemory<ValidationAllowance> ValidationAllowList { get; init; } // section 1
}

public readonly record struct ValidationAllowance(int MessageIdNumber, string Reason); // Reason: non-empty
```

- `Create` runs the loader setup (section 2) and touches BRUTAL's `Vulkan` class once, so a missing loader fails here, not later. Expected failures are `Result`s: no loader (`ErrorKind.Unavailable`, "Vulkan loader missing: <BRUTAL's message>"), an explicit or bundled loader that BRUTAL didn't bind to (`Unavailable`), a failed `setenv` (`Native`). Misuse throws: a null `Logger`, a relative path (`ArgumentException`), an allowance with an empty reason.
- `VulkanBackend` is immutable and holds no native object, as the contract's "stateless backend" requires: a preloaded loader stays loaded for the process lifetime, so there is nothing to dispose. Every native object lives in the `IBackendDevice` that `CreateDevice` returns.
- **Order at startup** (the Runtime host, [Architecture 3.2](../03-architecture.md#lifecycle)): `VulkanBackend.Create`, then `SdlPlatform.Create` with `SdlPlatformOptions.VulkanLibraryPath = backend.LoaderPath` ([platform note section 11](platform.md#11-what-platformsdl3-exposes)), then the window, then `GraphicsDevice.Create`. That is how SDL learns the loader path: Graphics.Vulkan chooses and preloads the loader, the host passes the path on, and Platform.Sdl3 sets `SDL_HINT_VULKAN_LIBRARY` before `SDL_Init`. Neither backend references the other.
- A second `Create` in one process is valid: the decisions are idempotent (an already-set `VK_DRIVER_FILES` is left alone; preloading the same path again returns the same library). A second `Create` naming a different loader than BRUTAL already bound fails with `Unavailable`.

**The loader step, as the host sees it.** The platform note leaves choosing and preloading the loader to Graphics.Vulkan and takes the chosen path through `SdlPlatformOptions.VulkanLibraryPath` ([platform note section 11](platform.md#11-what-platformsdl3-exposes)); the runtime host note (#57) calls this step first. `VulkanBackend.Create` *is* that step: the loader decision and the backend object come from one call, so no `IGraphicsBackend` can exist before its loader is settled. Creating it before any window costs nothing, because the backend is stateless (the graphics note's contract); the native objects come later, when `GraphicsDevice.Create` asks it for an `IBackendDevice` with the window as `CompatibleWindow`.

| Topic | Rule |
|-------|------|
| Names | `VulkanBackend.Create(VulkanBackendOptions)`; the result's `LoaderPath` is the absolute path of the loader it preloaded (bundled or explicit), or `null` for the system loader. The host passes it unchanged as `SdlPlatformOptions.VulkanLibraryPath` |
| Library lifetime | The preloaded library belongs to the process, not to the backend: it is never unloaded, because BRUTAL's `Vulkan` class binds it for the process lifetime (`Brutal.Vulkan/ImportResolver.cs:16-37`) and SDL loads the same file. Disposing every device leaves it loaded; nothing needs disposing |
| Failures | `Result` failures, never exceptions: loader missing or not loadable is `ErrorKind.Unavailable` ("Vulkan loader missing: ..."), the same kind the graphics note gives `GraphicsDevice.Create` for a missing runtime; BRUTAL bound a different loader than the chosen one is `Unavailable`; `setenv` failed is `Native`. Invalid options throw `ArgumentException` |
| Threading and order | Main thread, before the platform, any window and any other thread start (the environment and loader rules of section 2). Not checked by the backend: the host guarantees the order |
| Repeats | Idempotent for the same choice (above). A host with no graphics at all (a server) never calls it; any host that creates a `GraphicsDevice` needs the backend, so it can't skip the step |

## 1. Instance, validation and the debug messenger

Each `IBackendDevice` owns one `VkInstance` (the contract's ownership row), created through BRUTAL's `Instance` wrapper, the only way to get BRUTAL's instance function table (its constructor is internal). The wrapper throws `VkResultError` (`Brutal.Vulkan/Manual/Classes/VkResultError.cs:5`), so this one call site catches it and converts it to a `Result` (section 13). It also enumerates physical devices inside the constructor after the instance exists (`Brutal.Vulkan/Manual/Classes/Instance.cs:88-103`), so a failure there leaks the instance until BRUTAL's finalizer runs: upstream issue draft #20 (section 16).

**Extensions and layers** (ported from `samples/HelloWindow/VulkanContext.cs:118-123`, `:125-135`), all checked against `vkEnumerateInstanceExtensionProperties` through the raw global binding (`Brutal.Vulkan/Generated/PInvoke/VulkanApi/VulkanApi.gen.cs:50-55`, the two-call pattern with a `VK_INCOMPLETE` retry, as `samples/HelloWindow/Swapchain.cs:141-164` does for formats):

| Enabled | When |
|---------|------|
| The `WindowSurfaceSource`'s required extensions | the request has a `CompatibleSurface` (graphics note section 3) |
| `VK_KHR_portability_enumeration`, plus the `ENUMERATE_PORTABILITY` create flag | the loader offers it (MoltenVK is listed only then; unverified on macOS) |
| `VK_KHR_get_surface_capabilities2`, then `VK_KHR_surface_maintenance1` or `VK_EXT_surface_maintenance1` | offered; needed by swapchain maintenance1 (section 4) |
| `VK_EXT_debug_utils` | offered: object names and command labels cost nothing without it (graphics note section 13) |
| `VK_LAYER_KHRONOS_validation`, `VK_EXT_validation_features` | `request.Validation`; a missing layer fails with `Unavailable` ("validation layer not installed"), never runs unvalidated |

BRUTAL's `Instance.CreateInfo` sets have no defaults and throw a `NullReferenceException` when left null (`Brutal.Vulkan/Manual/Classes/Instance.cs:14-15`; upstream issue draft #6), so both sets are always passed. The application info asks for `VK_API_VERSION_1_3` and carries `request.ApplicationName`.

**Validation features.** With validation, `VkValidationFeaturesEXT` enables synchronization validation, as M0 does (`samples/HelloWindow/VulkanContext.cs:125-135`). GPU-assisted and best-practices validation stay off: they are slow, and best-practices warnings are advice, not errors, so they would fail the conformance suite's warnings-are-failures rule.

**The messenger is instance-owned.** The M0 counters are static, because an `[UnmanagedCallersOnly]` function pointer can't capture state (`samples/HelloWindow/ValidationMessenger.cs:9-17`; `tests/BrutalEngine.Aot.Smoke/ValidationMessenger.cs:12-18`). Engine assemblies may not hold static mutable state, so the state travels in `pUserData` (`Brutal.Vulkan/Generated/Structs/VkDebugUtilsMessengerCreateInfoEXT.gen.cs:66`):

```csharp
namespace BrutalEngine.Graphics.Vulkan;

internal sealed class ValidationSink                     // one per VkInstance; outlives the instance
{
    public ValidationSink(LogCategory category, ReadOnlyMemory<ValidationAllowance> allowList);
    public long Errors { get; }                          // Interlocked counters: the layer calls from any thread
    public long Warnings { get; }
    public long Allowed { get; }                         // allow-listed messages: logged at Debug, never counted above
    public nint UserData { get; }                        // GCHandle.ToIntPtr of a normal (not pinned) handle to this
    public void Release();                               // frees the GCHandle; only after vkDestroyInstance returned
}
```

- The device creates the sink first, puts `UserData` into the messenger create info, chains that create info into `VkInstanceCreateInfo` (so instance creation and destruction are validated and counted, as M0 does), then creates the persistent messenger through the raw instance table (`Brutal.Vulkan/Generated/Handles/VkInstance.gen.cs:114`). Teardown is the reverse: messenger, instance, then `Release`.
- The callback is a static `[UnmanagedCallersOnly]` method. It recovers the sink with `GCHandle.FromIntPtr(userData).Target`, increments a counter, and logs to the device logger's `Graphics.Validation` category: errors at `Error`, warnings at `Warning`, with `MessageIdName`, `MessageIdNumber` and the UTF-8 message (`Brutal.Vulkan/Generated/Structs/VkDebugUtilsMessengerCallbackDataEXT.gen.cs:53-65`) appended as UTF-8 spans, which allocates nothing (`src/BrutalEngine.Core/Logging/LogMessageHandler.cs:101`). It catches every exception (an exception escaping an `[UnmanagedCallersOnly]` method ends the process) and counts it as an error. It returns `VK_FALSE`.
- Severities are warning and error, of the general, validation and performance types, as in M0. `IBackendDevice.ValidationErrors` and `ValidationWarnings` read the sink of that device's own instance, so two devices in one test process never see each other's messages.
- **Allow-list.** `VulkanBackendOptions.ValidationAllowList` holds `MessageIdNumber` values (the layer's stable hash of the VUID) with a mandatory reason. An allowed message is logged at `Debug` with its reason and counted in `Allowed` only. The conformance suite keeps its list in one file, one entry per line with the comment the testing strategy requires ([GPU testing](../05-testing-strategy.md#gpu-testing-without-gpus)); the backend never ships a default list.
- **The negative canary** (`InjectValidationError`, a contract member): with validation on, it sets a flag that makes the next `Execute` record M0's wrong old layout in the first barrier of the frame (`samples/HelloWindow/FrameRenderer.cs:286-287`); with validation off it throws `InvalidOperationException`, as the contract says.

## 2. Loader setup

BRUTAL finds the loader in the static constructor of its `Vulkan` class (`Brutal.Vulkan/ImportResolver.cs:16-37`): `vulkan-1` from System32 on Windows (`:43-44`), `libvulkan.so.1` then `vulkan` on Linux, and on macOS `libvulkan.1.dylib`, Homebrew's folders and `VulkanEx` (`Brutal.Vulkan/ImportResolver.cs:49-57`) with `DllImportSearchPath.LegacyBehavior` (`:65`), which never searches the app directory. When nothing loads, the static constructor throws `DllNotFoundException` (`:69`), which surfaces as a `TypeInitializationException` on every later touch of `Vulkan`.

| OS | Loader | Driver |
|----|--------|--------|
| Windows | the driver's or SDK's `vulkan-1.dll` from System32, unless `LoaderPath` is given (preloading a file named `vulkan-1.dll` makes BRUTAL reuse it) | registry, untouched |
| Linux | the distro's `libvulkan.so.1` ([tech stack](../02-tech-stack.md)), unless `LoaderPath` is given | the system ICDs, untouched |
| macOS (unverified) | the bundled `libvulkan.1.dylib` in `AppDirectory`, preloaded by absolute path, unless `LoaderPath` is given | `VK_DRIVER_FILES` set to the bundled `MoltenVK_icd.json` unless `VK_DRIVER_FILES` or `VK_ICD_FILENAMES` is already set |

This ports `BundledVulkan` with its decisions unchanged: the driver variable first and the user's choice respected (`samples/HelloWindow/BundledVulkan.cs:95-114`), the preload checked by comparing BRUTAL's `vkGetInstanceProcAddr` with the preloaded library's export (`samples/HelloWindow/BundledVulkan.cs:125-140`, as `tests/BrutalEngine.Aot.Smoke/VulkanLoader.cs:31-41` does), and the variable set in both the C runtime and .NET, because .NET keeps its own copy of the environment on Unix (`samples/HelloWindow/BundledVulkan.cs:164-171`), through `setenv` from `/usr/lib/libSystem.B.dylib` (`:178-180`). An explicit `LoaderPath` is preloaded and checked the same way on every OS. A check that fails is `Unavailable` ("BRUTAL resolved a different Vulkan loader than <path>"), never a silent fallback. Setting `SDL_HINT_VULKAN_LIBRARY`, which `BundledVulkan` does itself, moves to Platform.Sdl3 through `LoaderPath` (section "The backend's public surface").

**The injectable host** keeps the macOS decisions unit-testable on Windows, like `BundledVulkan.IHost` (`samples/HelloWindow/BundledVulkan.cs:40-64`). The difference: every effect returns a `Result`, and the decision is returned, not stored.

```csharp
internal interface IVulkanLoaderHost
{
    bool IsMacOS { get; }
    bool FileExists(string path);
    string? GetEnvironmentVariable(string name);
    Result SetEnvironmentVariable(string name, string value);  // libc setenv, then Environment; Native on failure
    Result<nint> Preload(string path);                         // NativeLibrary.TryLoad + the vkGetInstanceProcAddr export
    nint BrutalGetInstanceProcAddr();                          // touches BRUTAL's Vulkan class; 0 if it failed to load
}

internal readonly record struct LoaderDecision(string? PreloadedPath, LoaderOrigin Origin, string? DriverFiles);
internal enum LoaderOrigin { System, Bundled, Explicit }

internal static class LoaderSetup
{
    public static Result<LoaderDecision> Configure<THost>(string appDirectory, string? explicitLoader, THost host)
        where THost : IVulkanLoaderHost;                       // the real host is a readonly struct: no boxing
}
```

**Process rules.** `setenv` is not thread-safe against concurrent `getenv`, and the loader reads `VK_DRIVER_FILES` when it first enumerates drivers, so `VulkanBackend.Create` runs on the main thread before the host starts any thread or SDL. The Runtime host guarantees that order; nothing in the backend can check it. The `Create` decision is logged once at `Information` ("Vulkan loader: <path> (bundled|explicit|system)", plus the driver files when set), which is also the CI proof that the bundled path was used.

## 3. Device selection and creation

**Candidates.** For each physical device of the instance (BRUTAL's `Instance.PhysicalDevices`), the backend reads the properties, the driver properties and the 1.3 features through `vkGetPhysicalDeviceProperties2`/`Features2`, chaining 1.3 structs only for a device that reports 1.3 (`samples/HelloWindow/VulkanContext.cs:245-255`). Extensions come from the raw `vkEnumerateDeviceExtensionProperties` two-call pattern: BRUTAL's `PhysicalDevice.ExtensionNames` throws on a failed enumeration (`Brutal.Vulkan/Manual/Classes/PhysicalDevice.cs:53`).

**Selection** is a pure function, `AdapterSelector.Select(IReadOnlyList<AdapterCandidate>, AdapterSelection, GraphicsFeatures required)`, ported from `samples/HelloWindow/DeviceSelector.cs:15-54`. A candidate is rejected, with one reason each, for:

- an API version below 1.3, or `dynamicRendering` or `synchronization2` missing;
- no queue family with graphics and compute, and with present support for the `CompatibleSurface` when there is one (`samples/HelloWindow/VulkanContext.cs:259-266` checked graphics and present only; compute is added because `ComputePipeline` runs on the same queue);
- `VK_KHR_swapchain` missing, when there is a `CompatibleSurface` (an offscreen-only device doesn't need it);
- each `RequiredFeatures` flag it lacks (`TimestampQueries` needs the chosen family's `timestampValidBits > 0` and a non-zero `timestampPeriod`).

The suitable candidates are ranked discrete > integrated > virtual > CPU, ties to the first enumerated (`samples/HelloWindow/DeviceSelector.cs:69-76`). `AdapterSelection` narrows the set before ranking: `ByIndex` (enumeration index), `ByName` (ordinal, case-insensitive substring), `ByType`. An override that matches no suitable candidate is `Unsupported` naming the override and listing every rejection; it never falls back. Every candidate and rejection is logged at `Information`. `GraphicsAdapterInfo.Driver` comes from a fixed `VkDriverId` name table, not `ToString`, because aliased enum values print either name.

**Queues.** One queue from the chosen family serves graphics, compute, transfer and present in M1 (section 7 explains why a transfer queue waits). `vkGetDeviceQueue` through BRUTAL's `Device.GetQueue` is called once at creation.

**Device extensions and the feature chain:**

| Enabled | When |
|---------|------|
| `VK_KHR_swapchain` | a `CompatibleSurface` was given |
| `VK_KHR_portability_subset` | offered: the specification requires it then (`samples/HelloWindow/VulkanContext.cs:218-223`; unverified on macOS) |
| `VK_KHR_swapchain_maintenance1`, else `VK_EXT_swapchain_maintenance1`, with its feature | offered, and the instance enabled surface maintenance1 (section 4) |
| `VK_EXT_memory_budget` | offered (section 6) |

`VkPhysicalDeviceFeatures2` carries `samplerAnisotropy`, `textureCompressionBC` and `textureCompressionASTC_LDR` when negotiated, then `VkPhysicalDeviceVulkan13Features` (`dynamicRendering`, `synchronization2`), then `VkPhysicalDeviceSwapchainMaintenance1FeaturesKHR` when used (`Brutal.Vulkan/Generated/Structs/VkPhysicalDeviceSwapchainMaintenance1FeaturesKHR.gen.cs:39`). The chain lives in stack locals that outlive the call, as in M0. Device creation goes through BRUTAL's `Device` wrapper, which builds the device function table (`Brutal.Vulkan/Manual/Classes/Device.cs:79-85`); it throws `VkResultError`, caught at this one call site.

**The portability subset** (unverified on macOS). When the extension is present, the backend reads `VkPhysicalDevicePortabilitySubsetFeaturesKHR` (`Brutal.Vulkan/Generated/Structs/VkPhysicalDevicePortabilitySubsetFeaturesKHR.gen.cs:53-137`) and the stride alignment property (`Brutal.Vulkan/Generated/Structs/VkPhysicalDevicePortabilitySubsetPropertiesKHR.gen.cs:39`). The interface exposes only four of its restrictions; each becomes an `Unsupported` result at creation time, never undefined behaviour:

| Restriction | Effect |
|-------------|--------|
| `mutableComparisonSamplers` false | `CreateSampler` with `Compare` returns `Unsupported` (comparison samplers would have to be immutable) |
| `vertexAttributeAccessBeyondStride` false | `CreateGraphicsPipeline` with an attribute past its stride returns `Unsupported` |
| `minVertexInputBindingStrideAlignment` | a stride that isn't a multiple returns `Unsupported` |
| `multisampleArrayImage` false | `CreateTexture` with `SampleCount > 1` and `ArrayLayers > 1` returns `Unsupported`, and `GetFormatSupport` is unchanged |

The other flags (triangle fans, point polygons, events, swizzles, LOD bias, 3D-as-2D views, separate stencil references, constant-alpha blend factors) concern features the interface doesn't expose.

**Capabilities.** `GraphicsLimits` come from `VkPhysicalDeviceLimits` exactly as the graphics note lists; `Features` are the required set plus the available optional ones. `GetFormatSupport` maps `optimalTilingFeatures` from `vkGetPhysicalDeviceFormatProperties2` (sampled, linear filter, colour attachment and blend, depth attachment, storage, transfer source and destination) plus the sample counts for `Multisample`. It is computed once per `TextureFormat` at creation and read from an array afterwards, so it never allocates.

| `TextureFormat` | `VkFormat` | `TextureFormat` | `VkFormat` |
|-----------------|-----------|-----------------|-----------|
| `R8Unorm` | `R8_UNORM` | `Rg16Float` | `R16G16_SFLOAT` |
| `Rg8Unorm` | `R8G8_UNORM` | `Rgba16Float` | `R16G16B16A16_SFLOAT` |
| `Rgba8Unorm` | `R8G8B8A8_UNORM` | `R32Float` | `R32_SFLOAT` |
| `Rgba8UnormSrgb` | `R8G8B8A8_SRGB` | `Rg32Float` | `R32G32_SFLOAT` |
| `Bgra8Unorm` | `B8G8R8A8_UNORM` | `Rgba32Float` | `R32G32B32A32_SFLOAT` |
| `Bgra8UnormSrgb` | `B8G8R8A8_SRGB` | `R32Uint` | `R32_UINT` |
| `R16Float` | `R16_SFLOAT` | `Rgba8Uint` | `R8G8B8A8_UINT` |
| `Depth16Unorm` | `D16_UNORM` | `Depth32Float` | `D32_SFLOAT` |

**Debug names.** `SetDebugName` calls `vkSetDebugUtilsObjectNameEXT` (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:418`) only when `VK_EXT_debug_utils` is enabled: the device table loads every entry point through `vkGetDeviceProcAddr` (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:1052-1054`), and an extension that isn't enabled leaves a null function pointer, so every extension call is gated on the extension being enabled.

## 4. Swapchains and presentation

**Surfaces.** `CreateSurface(source)` returns the selection surface when `source` is the request's `CompatibleSurface` instance (reference equality) and it hasn't been handed out yet. Otherwise it checks that every extension `source` requires is enabled on the instance, calls `source.CreateVulkanSurface(instance)` and keeps its `Result`, then checks present support for the device's queue family (`vkGetPhysicalDeviceSurfaceSupportKHR` through the raw instance table, `Brutal.Vulkan/Generated/Handles/VkInstance.gen.cs:70-74`). A missing extension or no present support destroys the new surface and returns `Unsupported`. `DestroySurface` is `vkDestroySurfaceKHR` from the same instance, which the platform contract assigns to the backend.

**Creation** ports `samples/HelloWindow/SwapchainPlanner.cs` as a pure `SwapchainPlanner` with the same rules, plus the interface's options:

- **Extent:** the surface's `currentExtent`, or the source's `PixelWidth`/`PixelHeight` clamped to the surface limits when the extent is undefined (headless, Wayland), checked for zero before clamping (`samples/HelloWindow/SwapchainPlanner.cs:17-37`). A zero extent at creation (the window was minimised after the front end's check) returns `Unavailable` ("surface extent is zero"), because a zero-sized swapchain is invalid.
- **Image count:** one more than the minimum, within the maximum (`samples/HelloWindow/SwapchainPlanner.cs:58-63`). **Composite alpha:** opaque when supported, else the lowest bit (`:91-101`).
- **Usage:** colour attachment, plus transfer destination when the surface offers it (`samples/HelloWindow/SwapchainPlanner.cs:44-52`), reported as `SwapchainInfo.SupportsCopyDestination`. A surface without colour-attachment usage is `Unsupported`.
- **Format:** `SwapchainFormat.Srgb` picks `B8G8R8A8_SRGB`, then `R8G8B8A8_SRGB`, then the `Unorm` pair, all with `SRGB_NONLINEAR`; `Unorm` picks the reverse order. The graphics note's "otherwise the first format" can't be honoured literally, because `SwapchainInfo.Format` is a `TextureFormat` and an arbitrary surface format may have none, so a surface with none of the four is `Unsupported`. Surface formats use the raw two-call query with the `VK_INCOMPLETE` retry (`samples/HelloWindow/Swapchain.cs:141-164`).
- **Present mode:** the requested mode when `vkGetPhysicalDeviceSurfacePresentModesKHR` lists it, otherwise `FIFO` with one `Information` log line per swapchain; `SwapchainInfo.PresentMode` reports the mode used.
- **Per swapchain:** a view per image, a render-finished semaphore per image (`samples/HelloWindow/Swapchain.cs:5-9`), an acquire semaphore per frame slot, and with maintenance1 a present fence per image, created signalled. Images are registered in the backend's texture and view tables, flagged as swapchain-owned, and returned by `GetSwapchainImage`.
- **Recreation:** `OldSwapchain` goes into `oldSwapchain`; the front end destroys the old one whether creation succeeded or not, after `WaitIdle` (`samples/HelloWindow/HelloWindowApp.cs:165-168`).

**Acquire and present use the raw table** (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:247-248`), as M0 does (`samples/HelloWindow/FrameRenderer.cs:88-89`), because BRUTAL's wrappers throw on `VK_ERROR_OUT_OF_DATE_KHR` (`Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs:8442`; upstream issue draft #1). `AcquireImage(swapchain, slot)` waits on the slot's acquire semaphore with a timeout of `GpuTimeout` (M0 waits forever, which hangs a CI job on a hung GPU; the timeout is a `nint` in BRUTAL's binding, upstream issue draft #5). The result maps as `FrameResultPolicy` did (`samples/HelloWindow/FrameResultPolicy.cs:14-24`), but into the contract's statuses:

| `VkResult` | `SwapchainStatus` |
|------------|-------------------|
| `SUCCESS` | `Success` |
| `SUBOPTIMAL_KHR` | `Suboptimal` (acquire: the image is acquired and its semaphore will signal) |
| `ERROR_OUT_OF_DATE_KHR` | `OutOfDate` |
| `ERROR_SURFACE_LOST_KHR` | `SurfaceLost` |
| `ERROR_DEVICE_LOST`, `TIMEOUT`, `NOT_READY`, any other error | `DeviceLost`, with the code in `NativeCode`: every other failure of a frame call is a loss by the contract's frame-call rule |

**Present fences.** With swapchain maintenance1, each `Present` chains `VkSwapchainPresentFenceInfoKHR` (`Brutal.Vulkan/Generated/Structs/VkSwapchainPresentFenceInfoKHR.gen.cs:45`; the EXT structure type is an alias of the KHR one, `Brutal.Vulkan/Generated/Enums/VkStructureType.gen.cs:10112`) with that image's fence, after waiting for and resetting it (signalled in the normal case, because the image was acquired again). `DestroySwapchain` then waits for every present fence, bounded by `GpuTimeout`, before destroying the semaphores and the swapchain, which makes destroying them provably safe. Without the extension it relies on the front end's `WaitIdle` before `DestroySwapchain`, as M0 does; that is the fallback the graphics note describes, and recreation waits for idle in M1 either way. The RTX 2070 SUPER's driver lists both extension names (the maintainer's `vulkaninfo`, 2026-11-26; cited, not run by this note); lavapipe is unknown until G5 runs it, so both paths are tested by switching the extension off through an internal option.

## 5. Frames in flight and shutdown

Each of the `FramesInFlight` slots (1-3, default 2) owns, created with the device:

| Object | Use |
|--------|-----|
| a fence, created signalled | completion of the slot's last submit; `WaitForFrame` and `IsFrameComplete` read it |
| a command pool (`TRANSIENT`) and one primary command buffer | reset as a whole at `BeginFrame` with `vkResetCommandPool` (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:77`) |
| a descriptor pool chain for frame bind groups | reset at `BeginFrame` (section 8) |
| a timestamp query pool | when `TimestampQueries` is enabled (section 11) |
| a `submitted` flag | whether the fence has a pending signal |

Command buffers come from `Brutal.Vulkan.Abstractions` (`Brutal.Vulkan.Abstractions/Extensions/DeviceExtensions.cs:362-376`), because core BRUTAL keeps the allocation function and the `CommandBufferCount` field internal (`Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs:2006`, `Brutal.Vulkan/Generated/Structs/VkCommandBufferAllocateInfo.gen.cs:52`; upstream issue draft #4). Recording goes through BRUTAL's public `CommandBuffer` struct (`Brutal.Vulkan/Manual/Classes/CommandBuffer.cs:10-14`), which shares the device table.

- **`WaitForFrame(slot)`** returns at once when `submitted` is false; otherwise `vkWaitForFences` with `GpuTimeout` (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:60-62`). `VK_TIMEOUT` is `ErrorKind.Timeout`, `VK_ERROR_DEVICE_LOST` is `Unavailable`, anything else `Native`; the front end treats all of them as loss. `IsFrameComplete` is `vkGetFenceStatus`: `SUCCESS` true, `NOT_READY` false, errors likewise.
- **`BeginFrame(slot)`** resets the command pool and the frame descriptor pools on the host. The command buffer is begun in `Execute`, which records the query pool's reset first (section 11), so the query pool can still grow before anything refers to it.
- **`Execute(plan)`** begins the command buffer (`ONE_TIME_SUBMIT`), records the plan (sections 9-11), ends it, waits on the acquire semaphore of every `plan.Acquired` image at `COLOR_ATTACHMENT_OUTPUT` and signals each image's render-finished semaphore at the same stage (`samples/HelloWindow/FrameRenderer.cs:115-116`), then resets the fence and calls `vkQueueSubmit2` with it, and sets `submitted`. The fence is reset only immediately before a submit, so a failure before that point leaves it signalled, which is the contract's "fails before submitting" rule (M0's rule, `samples/HelloWindow/FrameRenderer.cs:13-16`). If `vkQueueSubmit2` itself fails, the fence stays unsignalled, so `submitted` stays false and the next `WaitForFrame` returns at once instead of waiting forever.
- **Deferred destruction belongs to the front end:** it calls `Destroy` only once an object's last frame has completed, so the backend destroys at once. The backend's own per-frame objects (frame bind groups, query results) are reclaimed by the slot resets above, never by `Destroy`. The wait and signal arrays are sized by the number of acquired images: up to 8 on the stack, beyond that in a scratch block from `request.Allocator` that only grows, so no frame call allocates managed memory.

**Shutdown** (`IBackendDevice.Dispose`, after the front end's `WaitIdle` and leak report): `vkDeviceWaitIdle`, ignoring a lost device because destroying objects stays valid then (`samples/HelloWindow/HelloWindowApp.cs:225-235`); every persistent object still in the backend's tables (not swapchain images or frame bind groups), logged at `Error` as a backend leak, newest first: pipelines, bind groups and their pools, layouts, shader modules, samplers, views, textures and buffers (through VMA); swapchains (present fences, semaphores, views, then the swapchain) and surfaces; the per-slot objects; the pipeline cache; the VMA allocator; the device; the messenger; the instance; then the sink's `GCHandle`. BRUTAL's `Device.Dispose` and `Instance.Dispose` free their function tables without a guard (`Brutal.Vulkan/Manual/Classes/Device.cs:120-126`; M0 notes the same for the instance, `samples/HelloWindow/VulkanContext.cs:76`), so `Dispose` is idempotent through its own flag and calls each exactly once.
