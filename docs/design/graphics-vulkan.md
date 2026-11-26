# Design: Vulkan backend (`BrutalEngine.Graphics.Vulkan`)

## Purpose

`BrutalEngine.Graphics.Vulkan` is the only v1 backend of the render interface ([ADR-0002](../adr/0002-render-interface-vulkan-first.md), [Architecture 3.8](../03-architecture.md#38-graphics-render-interface-brutalenginegraphics-and-vulkan-backend)). The [render interface note](graphics.md) owns the public API and the backend contract (`BrutalEngine.Graphics.Backend`: `IGraphicsBackend`, `IBackendDevice`, the frame plan, the failure channels, surface and swapchain lifetime, device-loss rules). This note implements that contract in Vulkan 1.3 terms through BRUTAL's bindings and changes nothing in it. The [platform note](platform.md#4-vulkan-surface-source) owns the window's surface source; the backend sees it only as the Graphics-owned `WindowSurfaceSource`, so Graphics.Vulkan never references Platform (`tests/BrutalEngine.Architecture.Tests/LayerMap.cs:108-115`).

Every section says what M0 code it ports (`samples/HelloWindow`, `tests/BrutalEngine.Aot.Smoke`) and which issue of the split (section 17) builds it. The samples' sync is proven, but their error handling is sample-grade (exceptions for statuses, process-wide counters); nothing is copied without the rules below.

Rules that shape every section:

- **Statuses are values.** Every result-returning Vulkan or VMA call goes through one wrapper (section 13) and becomes a `Result` or a `SwapchainStatus`, as the contract requires. BRUTAL's convenience wrappers throw on error codes, including `VK_ERROR_DEVICE_LOST` (`Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs:8356-8365`), so the frame path never calls them.
- **No static mutable state** ([architecture tests](architecture-tests.md#rules)): the validation counters, the loader decision, the fault injector and every pool belong to an instance. The only statics are `const` values, pure functions, the `[UnmanagedCallersOnly]` callback (a function, not a field) and `GlobalCommands.Gate`, a readonly lock that holds no game type (section 1).
- **Owner thread only.** The contract calls every `IBackendDevice` member on the device's owner thread, never re-entrantly. So the backend takes no locks on a device's own work and calls the queue through the raw function table (section 13). Its one lock is between devices: the process-wide gate around the global commands of instance creation (section 1).
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

**Extensions and layers** (ported from `samples/HelloWindow/VulkanContext.cs:118-123`, `:125-135`). Instance extensions are checked against `vkEnumerateInstanceExtensionProperties` through the raw global binding (`Brutal.Vulkan/Generated/PInvoke/VulkanApi/VulkanApi.gen.cs:50-55`, the two-call pattern with a `VK_INCOMPLETE` retry, as `samples/HelloWindow/Swapchain.cs:141-164` does for formats): once with no layer name (the loader, drivers and implicit layers) and, with validation, once more with `pLayerName = "VK_LAYER_KHRONOS_validation"`, because the layer itself provides `VK_EXT_validation_features`. The layer's presence comes from `vkEnumerateInstanceLayerProperties`, the same two-call pattern (M0: `samples/HelloWindow/VulkanContext.cs:159-175`). The `VK_INCOMPLETE` retry is bounded: a list that keeps growing between the two calls is tried `InstanceExtensions.MaxEnumerationAttempts` (8) times, then the last `VK_INCOMPLETE` goes through the gate as a `Native` failure (`src/BrutalEngine.Graphics.Vulkan/Instances/InstanceExtensions.cs:25`, `:161-191`, `:203-233`).

| Enabled | When |
|---------|------|
| The `WindowSurfaceSource`'s required extensions | the request has a `CompatibleSurface` (graphics note section 3) |
| `VK_KHR_portability_enumeration`, plus the `ENUMERATE_PORTABILITY` create flag | the loader offers it (MoltenVK is listed only then; unverified on macOS) |
| `VK_KHR_get_surface_capabilities2`, then `VK_KHR_surface_maintenance1` or `VK_EXT_surface_maintenance1` (KHR preferred) | offered **and** `VK_KHR_surface` is enabled, which only a compatible surface's extensions do: the registry makes them depend on it (VUID-vkCreateInstance-ppEnabledExtensionNames-01388; `src/BrutalEngine.Graphics.Vulkan/Instances/InstanceExtensions.cs:63-75`); needed by swapchain maintenance1 (section 4) |
| `VK_EXT_debug_utils` | without validation: when offered (object names and command labels cost nothing without it, graphics note section 13). With validation: **required**, because the counters and the negative canary depend on the messenger; missing is `Unavailable` |
| `VK_LAYER_KHRONOS_validation`, and `VK_EXT_validation_features` from the layer's own extension list | `request.Validation`; a missing layer or extension fails with `Unavailable` ("validation layer not installed"), never runs unvalidated |

BRUTAL's `Instance.CreateInfo` sets have no defaults and throw a `NullReferenceException` when left null (`Brutal.Vulkan/Manual/Classes/Instance.cs:14-15`; upstream issue draft #6), so both sets are always passed. The application info asks for `VK_API_VERSION_1_3` and carries `request.ApplicationName`.

**Global commands are serialized** (#268). The Vulkan spec puts no external synchronization on `vkEnumerateInstanceExtensionProperties`, `vkEnumerateInstanceLayerProperties` or `vkCreateInstance`, but overlapping them in one process crashes it on Linux with the NVIDIA proprietary driver (Vulkan loader 1.4.363). Each of these calls loads the drivers, and while one thread is still inside the driver's `vk_icdNegotiateLoaderICDInterfaceVersion` (which opens an X connection), another thread's call jumps through a loader function pointer that isn't set yet (SIGSEGV at address 0). In a standalone repro, fresh processes whose 8 threads made these calls 10 times each crashed 50 times out of 50 with no lock, and 0 out of 50 with every one of them behind one lock. Locking only `vkCreateInstance` still crashed 50 out of 50, and leaving `vkDestroyInstance` unlocked crashed 0 out of 50. CI's Linux job, on Mesa's lavapipe, passed the same test suite in parallel before the fix. So:

- `GlobalCommands` (`src/BrutalEngine.Graphics.Vulkan/Instances/GlobalCommands.cs`) makes every global command the backend issues, through a `GlobalCommands.ISource` (the real one is `LoaderSource`; tests script or observe the calls), inside one process-wide `System.Threading.Lock`, `GlobalCommands.Gate`. The gate is held for the length of one command. For `vkCreateInstance` that is everything BRUTAL's `Vulkan.CreateInstance` does after the command: loading the instance table and enumerating the physical devices.
- Devices may be created on several threads at once: their global commands take turns. The gate is static because the race is between devices, so per-device locks wouldn't exclude each other. It is a `static readonly` field that holds no game type, which the static-state rule allows.
- `vkDestroyInstance`, the messenger and every instance and device command stay outside the gate. It covers only the backend's own calls: a global command another library in the process makes on its own doesn't pass through it.
- Tests (`tests/BrutalEngine.Graphics.Vulkan.Tests/Instances/GlobalCommandsTests.cs`): the test sources check `Gate.IsHeldByCurrentThread` inside every call, so a call made outside the gate fails whatever the timing; 8 threads creating instances at once never have two calls in progress; the gate is free again after a command returns or throws; and a source scan keeps every global command in `GlobalCommands.cs`.

**Validation features.** With validation, `VkValidationFeaturesEXT` enables synchronization validation, as M0 does (`samples/HelloWindow/VulkanContext.cs:125-135`). GPU-assisted and best-practices validation stay off: they are slow, and best-practices warnings are advice, not errors, so they would fail the conformance suite's warnings-are-failures rule.

**The messenger is instance-owned.** The M0 counters are static, because an `[UnmanagedCallersOnly]` function pointer can't capture state (`samples/HelloWindow/ValidationMessenger.cs:9-17`; `tests/BrutalEngine.Aot.Smoke/ValidationMessenger.cs:12-18`). Engine assemblies may not hold static mutable state, so the state travels in `pUserData` (`Brutal.Vulkan/Generated/Structs/VkDebugUtilsMessengerCreateInfoEXT.gen.cs:66`):

```csharp
namespace BrutalEngine.Graphics.Vulkan.Instances;   // the Instances folder: an Instance namespace would hide BRUTAL's Instance

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
  - As built: `InstanceFactory` does this, with the chain `VkValidationFeaturesEXT`, then the messenger description (`ValidationCallback.CreateInfo`), and `InstanceFactory.Options` carries the validation log category and the allow-list. `VulkanInstance` owns the messenger and the sink, exposes the counters (`ValidationErrors`, `ValidationWarnings`, `ValidationAllowed`, readable after disposal), and its idempotent `Dispose` destroys the messenger, then the instance, then calls `Release`. A hand-built validated selection without `VK_EXT_debug_utils` is misuse (`ArgumentException`, before any native call): the messenger entry points would be null.
  - When messenger creation fails, the instance is destroyed and then the sink released. When `vkCreateInstance` itself fails, the sink is released. When BRUTAL's `Instance` constructor throws after the native instance exists (its physical-device enumeration, `Brutal.Vulkan/Manual/Classes/Instance.cs:94-103`), the instance leaks until BRUTAL's finalizer destroys it (`:108-129`), and the chained messenger can call back during that destroy. So the sink's handle is deliberately kept, alive as long as the leaked instance. `InstanceFactory.IsCreateInstanceFailure` tells the two apart by BRUTAL's message (`Brutal.Vulkan/Generated/PInvoke/VulkanApi/VulkanApiEx.gen.cs:30-60`).
- The callback is a static `[UnmanagedCallersOnly]` method. It recovers the sink with `GCHandle.FromIntPtr(userData).Target`, increments a counter, and logs to the device logger's `Graphics.Validation` category: errors at `Error`, warnings at `Warning`, with `MessageIdName`, `MessageIdNumber` and the UTF-8 message (`Brutal.Vulkan/Generated/Structs/VkDebugUtilsMessengerCallbackDataEXT.gen.cs:53-65`) appended as UTF-8 spans, which allocates nothing (`src/BrutalEngine.Core/Logging/LogMessageHandler.cs:101`). It catches every exception (an exception escaping an `[UnmanagedCallersOnly]` method ends the process) and counts it as one more error. It returns `VK_FALSE`. A zero `pUserData`, or a freed handle (whose slot reads as null), does nothing.
- Severities are warning and error, of the general, validation and performance types, as in M0. `IBackendDevice.ValidationErrors` and `ValidationWarnings` read the sink of that device's own instance, so two devices in one test process never see each other's messages.
- **Allow-list.** `VulkanBackendOptions.ValidationAllowList` holds `MessageIdNumber` values (the layer's stable hash of the VUID) with a mandatory reason. An allowed message is logged at `Debug` with its reason and counted in `Allowed` only. The conformance suite keeps its list in one file, one entry per line with the comment the testing strategy requires ([GPU testing](../05-testing-strategy.md#gpu-testing-without-gpus)); the backend never ships a default list.
- **The negative canary** (`InjectValidationError`, a contract member): with validation off it throws `InvalidOperationException`, as the contract says. With validation on, it creates (once per device) a backend-owned 1x1 `R8G8B8A8_UNORM` canary image and sets a flag; the next `Execute` records, in its prologue (section 5), one image barrier on that image from `SHADER_READ_ONLY_OPTIMAL` to `GENERAL`, M0's wrong-old-layout violation (`samples/HelloWindow/FrameRenderer.cs:286-287`). The image is never in `SHADER_READ_ONLY_OPTIMAL`: it starts in `UNDEFINED`, and each injection leaves the layer tracking it in `GENERAL`, so the claimed old layout mismatches on the first injection and on every later one, and each `InjectValidationError` on a device produces a message (G9d's test injects twice on one device and checks both). The violation doesn't depend on what the frame records, so a frame with no barrier of its own still reports it. The canary image is destroyed with the device.

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

**Selection** is a pure function, `AdapterSelector.Select(IReadOnlyList<AdapterCandidate>, AdapterSelection, GraphicsFeatures required)`, ported from `samples/HelloWindow/DeviceSelector.cs:15-54`. It implements the graphics note's required set exactly as written (`docs/design/graphics.md:115`: Vulkan 1.3, swapchain, dynamic rendering, synchronization2, a graphics queue that can present to the compatible window). A candidate is rejected, with one reason each, for:

- an API version below 1.3, or `dynamicRendering` or `synchronization2` missing;
- `VK_KHR_swapchain` missing, whether or not there is a `CompatibleSurface`;
- no queue family with graphics support, and with present support for the `CompatibleSurface` when there is one (`samples/HelloWindow/VulkanContext.cs:259-266`);
- each `RequiredFeatures` flag it lacks (`TimestampQueries` needs the chosen family's `timestampValidBits > 0` and a non-zero `timestampPeriod`).

Compute support is an implementation detail and never rejects an adapter the graphics note accepts: among the families that pass, the backend picks the first that also supports compute, else the first. A device whose chosen family lacks compute returns `Unsupported` from `CreateComputePipeline`, a channel the interface already has. Two refinements are proposed to the graphics note instead of implemented ("Points for the graphics note" 8 and 9): requiring the swapchain extension only with a `CompatibleSurface`, and rejecting adapters without a graphics-and-compute family.

The suitable candidates are ranked discrete > integrated > virtual > CPU, ties to the first enumerated (`samples/HelloWindow/DeviceSelector.cs:69-76`). `AdapterSelection` narrows the set before ranking: `ByIndex` (enumeration index), `ByName` (ordinal, case-insensitive substring), `ByType`. An override that matches no suitable candidate is `Unsupported` naming the override and listing every rejection; it never falls back. Every candidate and rejection is logged at `Information`. `GraphicsAdapterInfo.Driver` comes from a fixed `VkDriverId` name table, not `ToString`, because aliased enum values print either name.

**Queues.** One queue from the chosen family serves graphics, transfer, present and (when the family supports it) compute in M1 (section 7 explains why a transfer queue waits). `vkGetDeviceQueue` through BRUTAL's `Device.GetQueue` is called once at creation.

**Device extensions and the feature chain:**

| Enabled | When |
|---------|------|
| `VK_KHR_swapchain` | always: selection requires it (above) |
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

**Debug names.** `SetDebugName` calls `vkSetDebugUtilsObjectNameEXT` (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:418`). Vulkan wants NUL-terminated names, while `SetDebugName`, `BackendObjectTable.GetLabel` and VMA's `SetAllocationName` work with spans, so every name is copied into a 256-byte stack buffer with a terminating NUL, cut at a UTF-8 character boundary when longer (no allocation). Names are set only when `VK_EXT_debug_utils` is enabled: the device table loads every entry point through `vkGetDeviceProcAddr` (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:1052-1054`), and an extension that isn't enabled leaves a null function pointer, so every extension call is gated on the extension being enabled.

## 4. Swapchains and presentation

**Surfaces.** `CreateSurface(source)` returns the selection surface when `source` is the request's `CompatibleSurface` instance (reference equality) and it hasn't been handed out yet. Otherwise it checks that every extension `source` requires is enabled on the instance, calls `source.CreateVulkanSurface(instance)` and keeps its `Result`, then checks present support for the device's queue family (`vkGetPhysicalDeviceSurfaceSupportKHR` through the raw instance table, `Brutal.Vulkan/Generated/Handles/VkInstance.gen.cs:70-74`). A missing extension or no present support destroys the new surface and returns `Unsupported`. `DestroySurface` is `vkDestroySurfaceKHR` from the same instance, which the platform contract assigns to the backend.

**Creation** ports `samples/HelloWindow/SwapchainPlanner.cs` as a pure `SwapchainPlanner` with the same rules, plus the interface's options:

- **Extent:** the surface's `currentExtent`, or the source's `PixelWidth`/`PixelHeight` clamped to the surface limits when the extent is undefined (headless, Wayland), checked for zero before clamping (`samples/HelloWindow/SwapchainPlanner.cs:17-37`). A zero extent at creation (the window was minimised after the front end's check) returns `Unavailable` ("surface extent is zero"), because a zero-sized swapchain is invalid.
- **Image count:** one more than the minimum, within the maximum (`samples/HelloWindow/SwapchainPlanner.cs:58-63`). **Composite alpha:** opaque when supported, else the lowest bit (`:91-101`).
- **Usage:** colour attachment, plus transfer destination when the surface offers it (`samples/HelloWindow/SwapchainPlanner.cs:44-52`), reported as `SwapchainInfo.SupportsCopyDestination`. A surface without colour-attachment usage is `Unsupported`.
- **Format** (all with `SRGB_NONLINEAR`): `SwapchainFormat.Srgb` picks the first offered of `B8G8R8A8_SRGB`, `R8G8B8A8_SRGB`, `B8G8R8A8_UNORM`, `R8G8B8A8_UNORM`; `Unorm` picks the first offered of `B8G8R8A8_UNORM`, `R8G8B8A8_UNORM`, `B8G8R8A8_SRGB`, `R8G8B8A8_SRGB`. The graphics note's "otherwise the first format" can't be honoured literally, because `SwapchainInfo.Format` is a `TextureFormat` and an arbitrary surface format may have none, so a surface with none of the four is `Unsupported`. Surface formats use the raw two-call query with the `VK_INCOMPLETE` retry (`samples/HelloWindow/Swapchain.cs:141-164`).
- **Present mode:** the requested mode when `vkGetPhysicalDeviceSurfacePresentModesKHR` lists it, otherwise `FIFO` with one `Information` log line per swapchain; `SwapchainInfo.PresentMode` reports the mode used.
- **Per swapchain:** a view per image, a render-finished semaphore per image (`samples/HelloWindow/Swapchain.cs:5-9`), an acquire semaphore per frame slot, and with maintenance1 a present fence per image, created signalled. Images are registered in the backend's texture and view tables, flagged as swapchain-owned, and returned by `GetSwapchainImage`.
- **Recreation:** `OldSwapchain` goes into `oldSwapchain`; the front end destroys the old one whether creation succeeded or not, after `WaitIdle` (`samples/HelloWindow/HelloWindowApp.cs:165-168`).

**Acquire and present use the raw table** (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:247-248`), as M0 does (`samples/HelloWindow/FrameRenderer.cs:88-89`), because BRUTAL's wrappers throw on `VK_ERROR_OUT_OF_DATE_KHR` (`Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs:8442`; upstream issue draft #1). `AcquireImage(swapchain, slot)` calls `vkAcquireNextImageKHR`, which *signals* the slot's acquire semaphore when the image is ready; its timeout, `GpuTimeout`, bounds the wait for an available image (M0 waits forever, which hangs a CI job on a hung GPU; the timeout is a `nint` in BRUTAL's binding, upstream issue draft #5). The result maps as `FrameResultPolicy` did (`samples/HelloWindow/FrameResultPolicy.cs:14-24`), but into the contract's statuses:

| `VkResult` | `SwapchainStatus` |
|------------|-------------------|
| `SUCCESS` | `Success` |
| `SUBOPTIMAL_KHR` | `Suboptimal` (acquire: the image is acquired and its semaphore will signal) |
| `ERROR_OUT_OF_DATE_KHR` | `OutOfDate` |
| `ERROR_SURFACE_LOST_KHR` | `SurfaceLost` |
| `TIMEOUT`, `NOT_READY` (acquire only) | `OutOfDate`, see below |
| `ERROR_DEVICE_LOST` | `DeviceLost`, with the code in `NativeCode` |
| any other error (out of memory, unknown) | `DeviceLost`, with its own code in `NativeCode`: `SwapchainStatus` has no failure value besides `DeviceLost` |

**An acquire timeout is not a loss.** The contract's frame-call rule (`docs/design/graphics.md:983`) covers `WaitForFrame`, `IsFrameComplete`, `BeginFrame` and `Execute`, not `AcquireImage`, and a timeout there can be a healthy GPU whose compositor holds the images (FIFO on a hidden window on some compositors). So `TIMEOUT` is reported as `OutOfDate`: the front end recreates the swapchain and retries once (`Recreated` or `Skipped`), and the frame completes without an image. The recreation's `WaitIdle` is bounded (section 5), so a GPU that is really hung turns into a `Timeout` there, which the graphics note's `GpuTimeout` rule (`docs/design/graphics.md:113`) treats as a loss with `TimedOut`. The cost: while a window's images stay withheld, each acquire blocks the owner thread for up to two `GpuTimeout`s (the acquire, then the retry after the recreation) before it reports `Skipped`, plus a recreation. The risk: the recreation's `WaitIdle` and `DestroySwapchain` also wait on present fences, and if a compositor that withholds images also delays their present fences (unverified), this path still ends as a `TimedOut` loss of a healthy GPU. A dedicated status is proposed to the graphics note ("Points for the graphics note" 1).

**Present fences.** With swapchain maintenance1, each `Present` chains `VkSwapchainPresentFenceInfoKHR` (`Brutal.Vulkan/Generated/Structs/VkSwapchainPresentFenceInfoKHR.gen.cs:45`; the EXT structure type is an alias of the KHR one, `Brutal.Vulkan/Generated/Enums/VkStructureType.gen.cs:10112`) with that image's fence, after waiting for and resetting it (signalled in the normal case, because the image was acquired again). `DestroySwapchain` then waits for every present fence, bounded by `GpuTimeout`, before destroying the semaphores and the swapchain, which makes destroying them provably safe. `DestroySwapchain` returns nothing, so a fence that runs out of time marks the device lost (a present that never completes is a hung GPU, by the `GpuTimeout` rule) and logs at `Error`; that swapchain's objects are then kept, never destroyed while possibly in use, and the device's next frame call reports the loss with `ErrorKind.Timeout`, so the front end sets `TimedOut`. Without the extension it relies on the front end's `WaitIdle` before `DestroySwapchain`, as M0 does; that is the fallback the graphics note describes, and recreation waits for idle in M1 either way. The RTX 2070 SUPER's driver lists both extension names (the maintainer's `vulkaninfo`, 2026-11-26; cited, not run by this note); lavapipe is unknown until G5 runs it, so both paths are tested by switching the extension off through an internal option.

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
- **`Execute(plan)`** begins the command buffer (`ONE_TIME_SUBMIT`), records a fixed prologue (first the timestamp query reset when `TimestampQueries` is enabled, section 11, then the validation canary's barrier when one is pending, section 1; this is the only place the order is defined), records the plan (sections 9-11), records the host-visibility barrier below when needed, ends the command buffer, waits on the acquire semaphore of every `plan.Acquired` image at `COLOR_ATTACHMENT_OUTPUT` and signals each image's render-finished semaphore at the same stage (`samples/HelloWindow/FrameRenderer.cs:115-116`), then resets the fence and calls `vkQueueSubmit2` with it. The fence is reset only immediately before a submit, so a failure before that point leaves it signalled, which is the contract's "fails before submitting" rule (M0's rule, `samples/HelloWindow/FrameRenderer.cs:13-16`).
- **The `submitted` flag** is cleared in the same step that resets the fence, just before `vkQueueSubmit2`, and set only when the submit succeeds. So it is true exactly when the fence has a pending signal: after a failed submit the fence is unsignalled and the flag false, and the next `WaitForFrame` returns at once instead of waiting forever.
- **Host visibility for readback.** A fence signal and a host wait don't make device writes visible to the host: the fence's memory dependency covers device access only, and host-coherent memory removes only the need for an invalidate. So whenever a frame wrote a `HostReadback` buffer, `Execute` records, as the frame's last command, one `VkMemoryBarrier2` from `COPY` / `TRANSFER_WRITE` to `HOST` / `HOST_READ`. Copies are the only writers it needs to cover, because a `Readback` buffer accepts only `CopyDestination` usage (`docs/design/graphics.md:317`). The recorder sets a per-frame flag when a copy's destination id is a `HostReadback` buffer (one table lookup per copy, which it already does). With that barrier, `WaitForFrame` or `IsFrameComplete` reporting the frame done makes its results readable, which is the contract's memory row (`docs/design/graphics.md:982`). Section 6 has the CPU-write direction.
- **`WaitIdle`** is bounded: it waits for every slot's fence whose `submitted` flag is set, and with maintenance1 every present fence, each with `GpuTimeout` (a timeout is `ErrorKind.Timeout`, a loss with `TimedOut` by the graphics note's rule), and only then calls `vkDeviceWaitIdle`. Every submit carries its slot's fence, so once those fences have signalled, `vkDeviceWaitIdle` has only presentation left to wait for and returns promptly. The front end's recreation and teardown waits therefore can't block forever on a hung GPU.
- **Deferred destruction belongs to the front end:** it calls `Destroy` only once an object's last frame has completed, so the backend destroys at once. The backend's own per-frame objects (frame bind groups, query results) are reclaimed by the slot resets above, never by `Destroy`. The wait and signal arrays are sized by the number of acquired images: up to 8 on the stack, beyond that in a scratch block from `request.Allocator` that only grows, so no frame call allocates managed memory.

**Shutdown** (`IBackendDevice.Dispose`, after the front end's `WaitIdle` and leak report): the bounded `WaitIdle` above, then `vkDeviceWaitIdle`, ignoring a reported loss because destroying objects stays valid then (`samples/HelloWindow/HelloWindowApp.cs:225-235`); every persistent object still in the backend's tables (not swapchain images or frame bind groups), logged at `Error` as a backend leak, newest first: pipelines, bind groups and their pools, layouts, shader modules, samplers, views, textures and buffers (through VMA); swapchains (present fences, semaphores, views, then the swapchain) and surfaces; the per-slot objects; the pipeline cache; the VMA allocator; the device; the messenger; the instance; then the sink's `GCHandle`. BRUTAL's `Device.Dispose` and `Instance.Dispose` free their function tables without a guard (`Brutal.Vulkan/Manual/Classes/Device.cs:120-126`; M0 notes the same for the instance, `samples/HelloWindow/VulkanContext.cs:76`), so `Dispose` is idempotent through its own flag and calls each exactly once.

**After a hung GPU (a `TimedOut` loss), the device is abandoned, not disposed.** Destroying objects that a hung GPU may still be using is undefined, and `vkDeviceWaitIdle` has no timeout. The runtime host note (#57) therefore abandons the device, swapchains, window and platform after a `TimedOut` loss, and calls neither `Recover` nor `Dispose`. This note assumes the same of every caller (no `IBackendDevice` whose loss was a timeout is disposed) and proposes it to the graphics note ("Points for the graphics note" 11); it changes no contract, and today the graphics note's `Recover` destroys all of the old device's native objects (`docs/design/graphics.md:748`). So the backend's `Dispose` stays safe when that happens: if one is disposed after a timeout loss, the backend remembers that its loss came from a timeout (a fence or present-fence wait that ran out), calls no wait at all, destroys nothing, logs one `Error` ("device abandoned after a GPU timeout: native objects leaked"), and keeps the sink's `GCHandle` alive, because the layer may still call back. It returns at once. A *reported* loss (`VK_ERROR_DEVICE_LOST`) is disposed normally, as above. The graphics note's `GraphicsDevice.Dispose` waits for idle (`docs/design/graphics.md:80`) and doesn't state this case ("Points for the graphics note" 11).

## 6. Memory

GPU memory goes through VMA ([Architecture 3.3](../03-architecture.md#33-memory)), from `Ahwoo.Brutal.Vulkan.Vma` 2026.10.3, whose nuspec depends on `Vulkan`, `Vulkan.Abstractions` and `Vulkan.Vma.Native` 2026.10.3 (nuget.org, checked 2026-11-26); the native package ships `vma.dll`, `libvma.so` and `libvma.dylib` for the three RIDs. G6 adds the central version.

**Only the raw, result-returning calls.** BRUTAL's convenience layer throws a bare `System.Exception`: `CreateVmaAllocator` when creation fails (`Brutal.Vulkan.Vma/Manual/VmaDeviceExtensions.cs:38-40`), `VmaAllocator.AllocateBuffer` when allocation fails (`Brutal.Vulkan.Vma/Manual/VmaAllocator.cs:90-91`), and `VmaAllocator.MapMemory`, which also ignores the region offset it is given (`Brutal.Vulkan.Vma/Manual/VmaAllocator.cs:46-53`). All are upstream issue draft #19. So:

- The allocator comes from `Vma.PInvoke.vmaCreateAllocator` (`Brutal.Vulkan.Vma/Generated/Vma.gen.cs:7-11`) with `VulkanApiVersion` 1.3, the instance, physical device and device, and a `VmaVulkanFunctions` holding `vkGetInstanceProcAddr` and `vkGetDeviceProcAddr`, filled the way BRUTAL's own helper does (`Brutal.Vulkan.Vma/Manual/VmaDeviceExtensions.cs:26-35`). `VMA_ALLOCATOR_CREATE_EXT_MEMORY_BUDGET_BIT` (`Brutal.Vulkan.Vma/Generated/Types/Enums/VmaAllocatorCreateFlags.gen.cs:10`) is set when `VK_EXT_memory_budget` (`Brutal.Vulkan/Generated/Constants/VK.gen.cs:447`) was enabled.
- Calls go through the generated extension block, which is generic over `IVmaAllocator` (`Brutal.Vulkan.Vma/Generated/VmaAllocatorEx.gen.cs:7-8`; the interface is `Brutal.Vulkan.Vma/Manual/VmaAllocator.cs:8-11`). The backend implements it on a private `readonly struct` holding the handle, so no `VmaAllocator` class, and none of its throwing members, exists. `CreateBuffer` (`Brutal.Vulkan.Vma/Generated/VmaAllocatorEx.gen.cs:966`) and `CreateImage` (`:1129`) return the `VkResult`; `DestroyBuffer` (`:1118`) and `DestroyImage` (`:1229`) return nothing.

**Memory placement** for the contract's `BackendMemory` (flags and usages at `Brutal.Vulkan.Vma/Generated/Types/Enums/VmaAllocationCreateFlags.gen.cs:9-16` and `Brutal.Vulkan.Vma/Generated/Types/Enums/VmaMemoryUsage.gen.cs:12-14`):

| `BackendMemory` | VMA usage | Flags | Required properties |
|-----------------|-----------|-------|---------------------|
| `DeviceLocal` | `AUTO_PREFER_DEVICE` | `WITHIN_BUDGET` | none |
| `DeviceLocalHostVisiblePreferred` | `AUTO_PREFER_DEVICE` | `WITHIN_BUDGET`; `HOST_VISIBLE` preferred | none: a placement hint (ReBAR), never mapped |
| `HostUpload` | `AUTO` | `HOST_ACCESS_SEQUENTIAL_WRITE`, `MAPPED`, `WITHIN_BUDGET` | `HOST_VISIBLE`, `HOST_COHERENT` |
| `HostReadback` | `AUTO` | `HOST_ACCESS_RANDOM`, `MAPPED`, `WITHIN_BUDGET`; `HOST_CACHED` preferred | `HOST_VISIBLE`, `HOST_COHERENT` |

- **Persistent mapping:** `MAPPED` gives `VmaAllocationInfo.pMappedData`, returned as `BackendBuffer.MappedPointer` and valid until the buffer is destroyed (the contract's memory row). Requiring `HOST_COHERENT` covers the CPU-to-GPU direction with no flush: a queue submission makes earlier host writes to coherent memory visible to the device. The GPU-to-CPU direction needs more than coherence: a fence doesn't make device writes visible to the host, so `Execute` adds a `HOST`/`HOST_READ` barrier whenever a frame wrote a `HostReadback` buffer (section 5); coherence then removes only the invalidate. The Vulkan specification guarantees at least one host-visible, host-coherent memory type on every device, so the requirement can't make an allocation impossible.
- **Budgets:** `WITHIN_BUDGET` makes an allocation that would exceed the heap's budget fail with `VK_ERROR_OUT_OF_DEVICE_MEMORY` (an `OutOfMemory` result) instead of paging silently. On every `OutOfMemory`, the backend logs every heap's usage and budget from `GetHeapBudgets` (`Brutal.Vulkan.Vma/Generated/VmaAllocatorEx.gen.cs:104`) at `Warning`. The interface has no budget statistics; that is a follow-up for the graphics note, not something this note adds.
- **Textures:** before `vmaCreateImage`, the backend checks the exact combination (format, usage, samples, layers, mips, optimal tiling) with `vkGetPhysicalDeviceImageFormatProperties2` (`Brutal.Vulkan/Generated/Handles/VkInstance.gen.cs:62`): `VK_ERROR_FORMAT_NOT_SUPPORTED` is `Unsupported`, as the graphics note's failure table requires. Images are 2D, `OPTIMAL` tiling, `EXCLUSIVE` sharing, initial layout `UNDEFINED`.
- **Result mapping** for every allocation: out of device or host memory is `OutOfMemory`, a lost device `Unavailable`, anything else `Native` with the code.

**The resource tracker and leak report.** The front end owns the public leak report (`ThrowIfLeaks`, graphics note section 13). Below it, the backend keeps one table per `BackendObjectKind`: an id (a Core `Handle` raw value from a per-kind `HandleAllocator`, so ids are never zero and a stale id is detected, [handles](handles.md)) maps to the native handles, the `VmaAllocation` and the debug name. Names reach VMA too (`SetAllocationName`, `Brutal.Vulkan.Vma/Generated/VmaAllocatorEx.gen.cs:510`). At `Dispose` every entry left is destroyed and logged at `Error` with its kind and name (section 5), and before `vmaDestroyAllocator` the backend checks VMA's own statistics: a non-zero allocation count is a backend bug, logged and exposed to `Graphics.Vulkan.Tests` through an internal counter.

## 7. Uploads

The front end owns the upload ring and its policy (graphics note section 6): the per-slot ring of `UploadRingBytesPerFrame`, the dedicated overflow blocks, the pending blocks for writes between frames, and their sizing. It creates them as `HostUpload` buffers through `CreateBuffer` and writes through `MappedPointer`. The backend's part:

- **The copies.** `CopyBufferToBuffer` and the buffer-texture copies become `vkCmdCopyBuffer2` and `vkCmdCopyBufferToImage2`/`vkCmdCopyImageToBuffer2`, with `bufferRowLength = BytesPerRow / texelSize` and `bufferImageHeight = 0`, one layer per region. The front end's rules (256-byte row pitch, texel-aligned offsets, depth only to buffers) already satisfy Vulkan's (graphics note section 6). The frame planner puts the frame's upload copies first, behind one barrier batch.
- **Alignment:** ring sub-allocations come from the front end, aligned to `MinUniformBufferOffsetAlignment` for uniforms and to the texel size and 4 for copies. The backend adds no rule.
- **Memory:** `HostUpload` is coherent (section 6), so a CPU write before `Execute` is visible to that frame's copies with no flush, and the ring needs no backend bookkeeping.

**The transfer queue waits for M2.** Decision (this note): M1 records every copy on the graphics queue, in the frame's own command buffer. Every M1 upload is small and per frame, the copies run before the frame's passes in the same submit, so the planner's barriers order them, and no device lacks the graphics queue. A dedicated transfer queue would need, per resource, a release barrier on the transfer queue, a semaphore, and an acquire barrier on the graphics queue, plus a separate submit, and would only pay off for large loads streamed in the background, which M1 doesn't have. It is designed now so M2 adds it behind the same contract: the device keeps a family with `TRANSFER` but no `GRAPHICS`/`COMPUTE` when one exists, M2 records the pending blocks of between-frame loads into a transfer command buffer released to the graphics family (`EXCLUSIVE` sharing, explicit ownership transfer), and the frame's submit waits on a timeline semaphore. This narrows [Architecture 3.8](../03-architecture.md#38-graphics-render-interface-brutalenginegraphics-and-vulkan-backend) ("a transfer queue when available") for M1; the deviation is listed below.

## 8. Descriptors

| Bind group | Pool strategy |
|------------|---------------|
| Persistent | A chain of pools created with `FREE_DESCRIPTOR_SET`, each sized for 256 sets (256 uniform buffers, 256 storage buffers, 512 sampled images, 64 storage images, 256 samplers; internal constants tuned from the conformance and overlay counts). Allocation tries the newest pool; `VK_ERROR_OUT_OF_POOL_MEMORY` or `VK_ERROR_FRAGMENTED_POOL` adds a pool and retries once. `Destroy` frees the set with `vkFreeDescriptorSets` |
| Frame | A chain of pools per frame slot, reset as a whole at `BeginFrame` with `vkResetDescriptorPool` (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:117-122`), so no frame group is ever freed singly (the contract's frame-slot row). Exhaustion adds a pool to the chain, once, and logs at `Information`; the chain is kept, and its storage is a native array from `request.Allocator`, so growth creates native objects only and a warm frame creates nothing |

- **Layouts:** one `VkDescriptorSetLayout` per `BindGroupLayoutDescription`, with each entry's binding number, count and stages. Binding types map one to one: uniform buffer, storage buffer (both storage kinds; read-only access is the shader's declaration), sampled image, storage image, sampler. Images and samplers stay separate descriptors, as Slang emits them, never combined.
- **Writes:** `CreateBindGroup` allocates the set, then one `vkUpdateDescriptorSets` call with the write, buffer-info and image-info arrays stack-allocated from the entry count (the layout bounds it). Image layouts in the writes are `SHADER_READ_ONLY_OPTIMAL` for sampled images and `GENERAL` for storage images, the layouts section 10 guarantees at use.
- **Transient data needs no dynamic offsets:** a frame group's transient entry reads as the ring's alias buffer and offset (graphics note section 8), so the offset is written into the descriptor. Dynamic uniform buffers aren't used.
- **Results:** a pool that can't grow (out of memory) is `OutOfMemory`, a lost device `Unavailable`.

## 9. Shader modules and pipelines

- **Shader modules:** the module's `ShaderCode` entry with `ShaderCodeFormat.SpirV` becomes a `VkShaderModule`; with none, `Unsupported`, as the graphics note says. The front end has already checked the SPIR-V structurally. A driver rejection is `InvalidData`: for shader module and pipeline creation, `VkCheck.Error` maps every code that isn't out of memory or a lost device to `InvalidData` instead of `Native`, a per-call override (section 13).
- **Layouts:** a `VkPipelineLayout` from the groups' set layouts and the one `PushConstantRange` (stages to `VkShaderStageFlags`).
- **Graphics pipelines use dynamic rendering:** no render pass object. The colour target formats, depth format and sample count go into `VkPipelineRenderingCreateInfo` on the create info's `pNext` (`Brutal.Vulkan/Generated/Structs/VkPipelineRenderingCreateInfo.gen.cs:48-66`). Viewport and scissor are the only dynamic states. Vertex buffer layouts, topology, raster, depth and blend states map field by field; entry point names are copied into NUL-terminated UTF-8 on the stack.
- **Pipeline cache:** one in-memory `VkPipelineCache` per device (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:105`), passed to every pipeline creation and destroyed with the device. Nothing is written to disk in M1, as the graphics note says; persisting `vkGetPipelineCacheData` under the user path is a later, additive change.
- **Coordinate conventions.** Decision (this note): clip space is y-up with depth 0 to 1, the WebGPU, Direct3D and Metal convention, so shaders written for this interface stay portable to later backends. The backend sets every viewport with a negative height (`y = Y + Height`, `height = -Height`, core since Vulkan 1.1). Framebuffer coordinates (`Viewport`, `ScissorRect`, copy regions) keep their top-left origin, and `FrontFace` maps directly to `VK_FRONT_FACE_*`, because the flip keeps on-screen winding. G9c's culling and triangle cases pin both, since the graphics note doesn't state a convention (a follow-up asks it to).
- **Push constants** copy the recorded bytes with `vkCmdPushConstants` (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:131`), straight from the command stream's trailing bytes. The matrix layout stays the shader's business (graphics note section 9). S4 found that Slang decorates a push-constant matrix `RowMajor` even when column-major was asked for (`docs/spikes/README.md:102`; upstream issue draft #17, probably Slang's documented naming flip). G9d pins the convention with a GPU round trip: it pushes a non-symmetric `Matrix3x2` and a 4x4 matrix, has a compute shader copy what it sees into a storage buffer, and compares element by element. A mismatch changes the cook command's matrix-layout option, never the backend.
- **Results:** out of memory is `OutOfMemory`, a lost device `Unavailable`, a driver compile failure `InvalidData`, a portability restriction `Unsupported` (section 3).

## 10. Barriers and layouts

The front end's planner (G10) decides every transition from what the frame recorded and hands them over as `Barrier` records of `ResourceTransition`s (graphics note section 10). The backend keeps no layout state of its own: each `ResourceState` maps to a fixed stage, access and image layout, and one `Barrier` record becomes one `vkCmdPipelineBarrier2` (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:193-195`), its buffer and image barriers built on the stack in batches of 64.

| `ResourceState` | Stages | Access | Image layout |
|-----------------|--------|--------|--------------|
| `Undefined` | `NONE` | `NONE` | `UNDEFINED` (contents discarded) |
| `CopySource` / `CopyDestination` | `COPY` (`CLEAR` and `BLIT` included for the destination) | `TRANSFER_READ` / `TRANSFER_WRITE` | `TRANSFER_SRC_OPTIMAL` / `TRANSFER_DST_OPTIMAL` |
| `VertexBuffer` | `VERTEX_ATTRIBUTE_INPUT` | `VERTEX_ATTRIBUTE_READ` | n/a |
| `IndexBuffer` | `INDEX_INPUT` | `INDEX_READ` | n/a |
| `IndirectBuffer` | `DRAW_INDIRECT` | `INDIRECT_COMMAND_READ` | n/a |
| `UniformRead` | the transition's shader stages | `UNIFORM_READ` | n/a |
| `StorageRead` / `StorageReadWrite` | the shader stages | `SHADER_STORAGE_READ` (+ `SHADER_STORAGE_WRITE`) | `GENERAL` |
| `SampledRead` | the shader stages | `SHADER_SAMPLED_READ` | `SHADER_READ_ONLY_OPTIMAL` |
| `ColorTarget` | `COLOR_ATTACHMENT_OUTPUT` | `COLOR_ATTACHMENT_READ`, `COLOR_ATTACHMENT_WRITE` | `COLOR_ATTACHMENT_OPTIMAL` |
| `DepthRead` / `DepthReadWrite` | `EARLY_FRAGMENT_TESTS`, `LATE_FRAGMENT_TESTS` | `DEPTH_STENCIL_ATTACHMENT_READ` (+ `_WRITE`) | `DEPTH_STENCIL_READ_ONLY_OPTIMAL` / `DEPTH_STENCIL_ATTACHMENT_OPTIMAL` |
| `Present` | `NONE` | `NONE` | `PRESENT_SRC_KHR` |
| (backend-internal: the end of a frame that wrote a `HostReadback` buffer; no `ResourceState`) | `HOST`, from `COPY` | `HOST_READ`, from `TRANSFER_WRITE` | n/a: one `VkMemoryBarrier2` (section 5) |

- Shader stages map `Vertex`, `Fragment` and `Compute` to the matching `*_SHADER` stage bits. The depth layouts are the Vulkan 1.0 combined ones, valid for depth-only formats, so the separate-depth-stencil-layouts feature isn't needed. Every barrier uses `QUEUE_FAMILY_IGNORED`: one queue (section 7).
- **Swapchain images join the acquire and present semaphores**, exactly as M0 proved clean under synchronization validation (`samples/HelloWindow/FrameRenderer.cs:277-294`, `samples/HelloWindow/FrameRenderer.cs:318-333`): a transition out of `Undefined` on an acquired image uses `COLOR_ATTACHMENT_OUTPUT` as its source stage, the stage the acquire semaphore is waited at, so it chains after the presentation engine's release; the transition to `Present` uses `COLOR_ATTACHMENT_OUTPUT` as its destination stage with no access, the stage the render-finished semaphore signals at. The backend applies both stage rules to any transition of an image listed in `FramePlan.Acquired`, whatever the planner's states say.
- **`ClearSwapchainImage`** (an acquired image no pass wrote) is recorded as an empty dynamic-rendering pass with `LOAD_OP_CLEAR` to opaque black, which needs only colour-attachment usage. So the planner surrounds it with `ColorTarget` transitions; G10 must follow this rule, which is proposed to the graphics note ("Points for the graphics note" 12).
- **Render passes** are `vkCmdBeginRendering` (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:208`) with one `VkRenderingAttachmentInfo` per attachment, in the layouts above; the viewport and scissor are reset to the full attachment at each begin (graphics note section 9).

## 11. Timestamps

- **Capability:** `TimestampQueries` is offered when the chosen queue family's `timestampValidBits` is non-zero (`Brutal.Vulkan/Generated/Structs/VkQueueFamilyProperties.gen.cs:39`) and `timestampPeriod` is positive (`Brutal.Vulkan/Generated/Structs/VkPhysicalDeviceLimits.gen.cs:681`). `GraphicsLimits.TimestampPeriodNanoseconds` is `timestampPeriod` when `TimestampQueries` is enabled and 0 otherwise (`docs/design/graphics.md:172`), even on a device whose limits report a period. `timestampComputeAndGraphics` isn't needed: the one family is checked directly.
- **Pools:** each frame slot owns one `VK_QUERY_TYPE_TIMESTAMP` pool, 256 queries to start. The front end assigns two indices per labelled pass (the contract's timestamps row); when a plan uses an index past the pool's size, `Execute` replaces the slot's pool with one twice as large before it begins the command buffer (the slot's previous frame has completed and nothing recorded refers to the old pool, so it is destroyed at once). That frame creates one native query pool and allocates no managed memory; later frames create nothing.
- **Writes:** `Execute` resets the used range with `vkCmdResetQueryPool` in its prologue (section 5 defines the order), which is this backend's reset of the slot's query pool (the contract's `BeginFrame` comment) (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:92`), then writes `vkCmdWriteTimestamp2` at `TOP_OF_PIPE` before a pass begins and at `BOTTOM_OF_PIPE` after it ends, outside the rendering scope. G11 confirms these stage choices under validation.
- **Reads, never waiting:** `TryReadTimestamps(slot, ticks)` returns `false` while the slot's frame isn't complete (`IsFrameComplete`), otherwise calls `vkGetQueryPoolResults` (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:67`) with `64_BIT` and no `WAIT` flag. `NOT_READY` is `false`; so is a lost device, which the next frame call reports. Ticks are masked to `timestampValidBits` before they are returned, so the front end's subtraction never sees garbage high bits. The mask is `ulong.MaxValue` for 64 valid bits and `(1UL << bits) - 1` below that: C# masks shift counts to 6 bits, so `(1UL << 64) - 1` would be 0 and zero every tick (the RTX reports 64). With two frames in flight, frame N's results are read about two frames later, as the graphics note says.
- **Labels:** a labelled pass and every debug group become `vkCmdBeginDebugUtilsLabelEXT`/`EndDebugUtilsLabelEXT` (`Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:423-424`) when `VK_EXT_debug_utils` is enabled, with the label's UTF-8 name from `BackendObjectTable.GetLabel`.
- The RTX 2070 SUPER reports `timestampValidBits = 64` and `timestampPeriod = 1` (the graphics note, from the maintainer's `vulkaninfo`); lavapipe is unknown until G11 runs there, and a device without timestamps takes the documented unsupported path.

## 12. Device loss

Decision (maintainer, 2026-11-26): detection everywhere, a typed `DeviceLost` event, and teardown and recreation through owner callbacks, with an internal fault-injection seam. The front end owns the event, `Recover` and its own seam (graphics note section 12). The backend's part:

- **Detection in one place.** Every result-returning call goes through the device's call gate (section 13). `VK_ERROR_DEVICE_LOST` (`Brutal.Vulkan/Generated/Enums/VkResult.gen.cs:185`) from any of them sets the device's lost flag and returns the contract's loss value: `ErrorKind.Unavailable` with the native code, or `SwapchainStatus.DeviceLost`. A fence wait that runs out of `GpuTimeout` is `ErrorKind.Timeout`, which the front end reports as a loss with `TimedOut`. Once lost, frame calls and creations return without calling the driver, with the kind of the loss the device remembers: `ErrorKind.Timeout` after a timeout loss (a fence or present-fence wait that ran out, including the one in `DestroySwapchain`, section 4), `Unavailable` otherwise, and `SwapchainStatus.DeviceLost` from swapchain calls either way; destruction still calls it, because destroying objects stays valid after a loss.
- **Teardown and recreation** after a reported loss (after a timeout the device is abandoned instead, section 5). `Recover` (front end) calls the owners' `OnDeviceLost`, discards every id it held, disposes the `IBackendDevice` (section 5's order; `vkDeviceWaitIdle` returns `VK_ERROR_DEVICE_LOST` and is ignored), then calls `CreateDevice` with the same request and adapter selection. That builds a new instance, messenger, device, allocator and slots, and the front end recreates each swapchain through `CreateSurface` and `CreateSwapchain` before the owners' `OnDeviceRecreated`. Nothing survives in the backend; the old device's validation counts are added into the front end's statistics before it is disposed (G2b's rule that statistics survive).
- **The backend's fault seam** proves its own detection, which the front end's seam (which replaces a status after the backend returned it) can't reach. Decision (this note): it is compiled into release builds, internal, and reachable only from `BrutalEngine.Graphics.Vulkan.Tests` through `InternalsVisibleTo`. The shipped code is then exactly the tested code, and the cost is one null check per call. An injected result replaces the native call, so no real object is created or submitted behind it.

```csharp
internal sealed class VulkanFaultInjector              // null in production; set only by Graphics.Vulkan.Tests
{
    public void FailNth(VulkanCall call, int occurrence, VkResult result); // the occurrence-th call (1-based) returns result
    public bool TryTake(VulkanCall call, out VkResult result);            // consumed by the call gate before the native call
}

internal enum VulkanCall                               // one member per result-returning call the backend makes
{
    // instance and adapters (sections 1-3)
    EnumerateInstanceExtensionProperties, EnumerateInstanceLayerProperties, CreateInstance, CreateDebugUtilsMessenger,
    EnumerateDeviceExtensionProperties, GetImageFormatProperties2, CreateDevice,
    // surfaces and swapchains (section 4)
    GetSurfaceSupport, GetSurfaceCapabilities, GetSurfaceFormats, GetSurfacePresentModes, CreateSwapchain,
    GetSwapchainImages, AcquireNextImage, QueuePresent, WaitForPresentFence,
    // frames (section 5)
    CreateCommandPool, AllocateCommandBuffers, CreateFence, CreateSemaphore, WaitForFences, GetFenceStatus, ResetFences,
    ResetCommandPool, BeginCommandBuffer, EndCommandBuffer, QueueSubmit2, DeviceWaitIdle,
    // memory and resources (sections 6-7)
    CreateVmaAllocator, CreateBuffer, CreateImage, CreateImageView, CreateSampler,
    // descriptors and pipelines (sections 8-9)
    CreateDescriptorSetLayout, CreateDescriptorPool, ResetDescriptorPool, AllocateDescriptorSets, FreeDescriptorSets,
    CreateShaderModule, CreatePipelineLayout, CreatePipelineCache, CreateGraphicsPipelines, CreateComputePipelines,
    // timestamps and diagnostics (sections 3, 11)
    CreateQueryPool, GetQueryPoolResults, SetDebugUtilsObjectName,
}
```

- **The enum is complete:** every result-returning Vulkan or VMA call the design makes has a member, including the three BRUTAL-wrapper creation calls (`CreateInstance`, `CreateDevice`, `AllocateCommandBuffers`), whose gate check runs before the wrapper. An issue that adds a call adds its member in the same commit; review checks it (rule 1 in section 13).
- **What forces it:** `Graphics.Vulkan.Tests` (GPU trait, section 15) injects `VK_ERROR_DEVICE_LOST` at every `VulkanCall` and `VK_TIMEOUT` at `WaitForFences` and `WaitForPresentFence`, and checks the returned value, the lost flag, and a clean `Dispose` (validation clean, no VMA allocation left). The conformance suite's device-lost category forces loss through the front end's seam at fence wait, submit, acquire, present and resource creation, and runs `Recover` for real on lavapipe and the RTX (graphics note section 15).

## 13. Status checking

[Architecture 3.5](../03-architecture.md#35-error-handling) asks for one checking helper per library. For Vulkan and VMA it has two parts: a pure mapping and a per-device gate.

```csharp
internal static class VkCheck                          // pure; unit-tested as a table
{
    public static ErrorKind KindOf(VkResult result);    // below
    public static SwapchainStatus SwapchainStatusOf(VkResult result); // section 4's table
    public static EngineError Error(VkResult result, VulkanCall call); // message: a constant per call; no allocation
    public static EngineError FromException(VkResultError error, string call); // BRUTAL-wrapper creation sites only
}

internal sealed class VulkanCallGate                   // one per device; every result-returning call passes through it
{
    public bool IsLost { get; }
    public VulkanFaultInjector? Faults { get; set; }    // section 12
    public bool TryTakeFault(VulkanCall call, out VkResult result);
    public Result Check(VkResult result, VulkanCall call); // marks loss; allocates nothing on success or failure
}
```

`KindOf`: out of host or device memory, and `OUT_OF_POOL_MEMORY` once its retry failed, are `OutOfMemory`; `DEVICE_LOST` is `Unavailable`; `TIMEOUT` is `Timeout`; `FORMAT_NOT_SUPPORTED`, `FEATURE_NOT_PRESENT`, `EXTENSION_NOT_PRESENT`, `LAYER_NOT_PRESENT` and `INCOMPATIBLE_DRIVER` are `Unsupported`; `INVALID_SHADER_NV` is `InvalidData`; everything else is `Native`, always with the code in `NativeCode`. One per-call override: for `CreateShaderModule`, `CreateGraphicsPipelines` and `CreateComputePipelines`, every code that `KindOf` maps to `Native` becomes `InvalidData` (the driver rejected the code or state), so section 9's mapping and this table agree.

Rules (G3a writes them into the project's design section; review enforces them):

1. **Raw function tables for every result-returning call** after creation: `device.PInvoke.vkX.Invoke(...)`, `instance.PInvoke...`, and `Vulkan.PInvoke` for global calls (made only in `GlobalCommands`, inside its process-wide gate, section 1), then `VulkanCallGate.Check`. Every such call has its own `VulkanCall` member (section 12), so the fault injector reaches it. BRUTAL's generated wrappers throw `VkResultError` for error codes, including a lost device in `WaitForFences` (`Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs:1197`), and the `_Count` enumeration helpers drop their result (upstream issue draft #18), so neither is used.
2. **Three BRUTAL wrappers are allowed**, each at one creation-time call site that catches `VkResultError` and converts it with `VkCheck.FromException`: `Vulkan.CreateInstance` and `PhysicalDevice.CreateDevice` (their classes own the function tables and have internal constructors) and `Brutal.Vulkan.Abstractions`' `AllocateCommandBuffers` (section 5). The only other caught exception is the `TypeInitializationException` of a missing loader, in `VulkanBackend.Create`. No exception is ever caught on the frame path.
3. **Void commands** (`vkCmd*`, `vkDestroy*`, `vkUpdateDescriptorSets`) also go through the raw table in `Execute`, so no wrapper code sits between the command stream and the driver.
4. **Extension entry points** are called only when their extension is enabled (section 3).
5. **VMA:** only the generated, result-returning calls (section 6), through the same gate.
6. **Queue locking:** BRUTAL's `Queue` takes a lock around submit and present (`Brutal.Vulkan/Manual/Classes/Queue.cs:81-98`; upstream issue draft #7). The backend calls `vkQueueSubmit2` and `vkQueuePresentKHR` through the raw table, which skips that lock; that is sound because the contract confines every call to the owner thread, and nothing in the backend calls a locking `Queue` member (M0 relies on the same, `samples/HelloWindow/FrameRenderer.cs:140-143`).
7. **`uint64_t` timeouts** are `nint` in BRUTAL's binding (`Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs:1169-1173`; upstream issue draft #5): `GpuTimeout` in nanoseconds is passed as `unchecked((nint)ns)`, which is exact on the 64-bit RIDs the engine supports.
8. **A discarded status fails the build:** the BE1001 analyzer flags a discarded `Result` or `VkResult` (`src/BrutalEngine.Analyzers/DiscardedStatusResultAnalyzer.cs:39`).

## 14. NativeAOT

M0 proved under NativeAOT, with 0 trim and AOT warnings: BRUTAL's loader resolution and preloading, instance and device creation, the static messenger callback, raw acquire and present, `Brutal.Vulkan.Abstractions`' command buffer allocation (HelloWindow's frame loop) and a headless swapchain. BRUTAL declares no assembly AOT-compatible (upstream issue draft #12), so each new path needs an AOT run:

| New path | First issue | AOT coverage |
|----------|-------------|--------------|
| The messenger callback reaching an instance through a `GCHandle` in `pUserData` | G3e | G3e moves the smoke harness's `ValidationMessenger` (`tests/BrutalEngine.Aot.Smoke/ValidationMessenger.cs:12-18`) to the same pattern, so its existing scenario runs it on every smoke OS |
| `VulkanBackend.Create`, the loader host, `setenv` on macOS (unverified) | G3c | a new `graphics-vulkan` smoke scenario that creates the backend and logs its decision |
| VMA: `[LibraryImport("vma")]`, resolved by .NET's default native probing (the package installs no resolver), from `runtimes/<rid>/native` under JIT and the published folder under AOT | G6 | the scenario creates a buffer and maps it |
| The full device through `GraphicsDevice`: frames, a clear, a readback | G9f | the scenario renders offscreen with validation and checks `GraphicsStatistics.ValidationErrors` is 0 |

The smoke's negative canary stays in the existing raw scenario: the backend's `InjectValidationError` is reachable only through `IBackendDevice` or the front end's internal seam. The backend itself uses no reflection, `Enum.ToString` or runtime code generation (driver names come from a table, section 3). `.github/scripts/ci-tier.sh` already gives `src/BrutalEngine.Graphics*` the full tier (`.github/scripts/ci-tier.sh:14`); G3a adds a self-test case for the new project's path.

## 15. Testing

| Where | What | Runs on |
|-------|------|---------|
| `BrutalEngine.Graphics.Vulkan.Tests`, no driver | The pure parts: `LoaderSetup` with a fake host (every macOS branch, on any OS), `AdapterSelector`, `SwapchainPlanner`, the status tables (`KindOf`, `SwapchainStatusOf`), the `ResourceState`, format, vertex-format, blend and sampler mappings, portability restrictions over hand-built feature structs, descriptor-pool and query-pool growth, the allow-list | the build-test job, every OS |
| The same project, `Gpu` trait | Per-instance validation counts (two devices, a canary in one), allow-list suppression, selection on real adapters with each override, swapchains with maintenance1 on and off (windowed on Windows, headless on Linux), fences and `GpuTimeout`, VMA placement and leak checks, the host-read barrier, every fault injection (section 12), 1,000 steady frames allocating 0 managed bytes. Surfaces: `WindowSurfaceSource` has an internal constructor (`docs/design/graphics.md:221`) and the `InternalsVisibleTo` exception covers only Graphics.Tests and the conformance suite (`docs/design/graphics.md:752`), so components before G9e take a raw `VkSurfaceKHR` from a window the test creates through BRUTAL's SDL binding (a test-only reference). From G9f, swapchain and steady-frame tests go through `GraphicsDevice` with a test `IWindow`/`IVulkanSurfaceSource` over that binding, or over Platform.Sdl3 once P3 lands | the vulkan-smoke job on Linux and Windows lavapipe (the build-test job excludes the trait; G3d adds both filters); locally on the RTX |
| `BrutalEngine.Graphics.Conformance` (C1) | The backend-neutral contract through `GraphicsDevice`: validation-clean with synchronization validation, the negative canary, leak checks, readback and golden images (graphics note section 15) | the same CI drivers; locally on the RTX |
| `BrutalEngine.Aot.Smoke` | Section 14's scenarios, JIT and NativeAOT | the vulkan-smoke job |

- **Real GPU:** decision (maintainer, 2026-11-26): there is no self-hosted runner in M1. Each graphics PR records a local run on the RTX 2070 SUPER (Windows 11): verify.sh with `--gpu`, the `Gpu`-trait tests and the conformance suite. The software-Vulkan nightly runs the same suites. The decision is revisited at M1 close.
- **CI drivers:** lavapipe on ubuntu-24.04 (layers 1.3.275) and windows-2025 (layers 1.4.363), so validation coverage differs between them ([spikes, S3](../spikes/README.md#s3-software-vulkan-on-githubs-linux-and-windows-runners)). Headless presentation runs on Linux only: no Windows driver offers `VK_EXT_headless_surface`.
- **macOS:** unverified in M1. The portability rules are unit-tested with hand-built feature structs; nothing runs on MoltenVK until macOS returns to CI.
- **Negative canaries:** `InjectValidationError` in the conformance suite and the `Gpu` tests; the raw AOT canary in the smoke harness. Each proves the layer is loaded where it runs.

## 16. BRUTAL gaps

Every gap below is worked around as described. The numbers are entries in the maintainer's draft of upstream issues (unfiled), so a fix upstream can be traced back here.

| Draft | Gap | Workaround (section) |
|-------|-----|----------------------|
| #1 | Swapchain wrappers throw on `OUT_OF_DATE` (`Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs:8442`), and every generated wrapper throws on `DEVICE_LOST` (`Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs:8356-8365`) | raw function tables (4, 13) |
| #3 | The macOS loader is never found next to the app (`Brutal.Vulkan/ImportResolver.cs:65`); a missing loader throws from a static constructor (`Brutal.Vulkan/ImportResolver.cs:69`) | preload by absolute path; catch the type-initialisation failure once (2) |
| #4 | Command buffer allocation and memory mapping are internal in core (`Brutal.Vulkan/Generated/Extensions/VkDeviceExtensions.gen.cs:393`) | `Brutal.Vulkan.Abstractions` for allocation; VMA's persistent mapping (5, 6) |
| #5 | `uint64_t` timeouts bound as `nint` | `unchecked((nint)ns)` on 64-bit RIDs (13) |
| #6 | `Instance.CreateInfo` sets have no defaults | always pass both sets (1) |
| #7 | `Queue` locks around submit and present | raw table on the owner thread (13) |
| #12 | No assembly declares AOT compatibility | an AOT smoke run per new path (14) |
| #17 | Slang decorates push-constant matrices `RowMajor` | a GPU round-trip test pins the convention (9) |
| #18 | Enumeration helpers hide the written count and drop the result | raw two-call queries with a `VK_INCOMPLETE` retry (1, 3, 4) |
| #19 (new) | VMA's convenience wrappers throw a bare `System.Exception` (`Brutal.Vulkan.Vma/Manual/VmaDeviceExtensions.cs:38-40`, `Brutal.Vulkan.Vma/Manual/VmaAllocator.cs:90-91`), and `VmaAllocator.MapMemory` ignores the region offset (`Brutal.Vulkan.Vma/Manual/VmaAllocator.cs:46-53`) | raw `vmaCreateAllocator` and the generated calls through an own `IVmaAllocator` struct (6) |
| #20 (new) | `Instance`'s constructor enumerates devices after creating the instance, so a failure leaks it until the finalizer (`Brutal.Vulkan/Manual/Classes/Instance.cs:88-103`); `Device.Dispose` and `Instance.Dispose` free their function tables without a guard (`Brutal.Vulkan/Manual/Classes/Device.cs:120-126`) | catch at the one call site; an idempotent `Dispose` of our own (1, 5) |

## 17. The issue split

All Graphics.Vulkan issues edit one project and its `PublicAPI.Unshipped.txt`, so they run in sequence. Until the device facade (G9e) lands, each issue builds internal components and tests them directly through `InternalsVisibleTo`, with their GPU cases under the `Gpu` trait; no `IBackendDevice` member ever exists as a stub. `VulkanBackend` gains its public members as they become real: `Create`, `Name` and `LoaderPath` in G3c, `EnumerateAdapters` in G4, and `IGraphicsBackend` with `CreateDevice` in G9e.

G3 and G9 are estimated line by line (source, XML docs, tests, then PublicAPI, CI and harness lines), because recent issues ran 2-2.5 times over source-only estimates; each part over about 600 became its own row. G4-G8 keep their first estimates and get the same treatment when they are drafted.

| Issue | Scope from this note | Blocked by | Est. (src + docs + tests + other) |
|-------|----------------------|------------|------|
| G3a feat(graphics-vulkan): check Vulkan statuses through one gate | Creates `src/BrutalEngine.Graphics.Vulkan` and its test project; `VulkanCall`, `VkCheck` (without the swapchain mapping, which G5 adds), `VulkanCallGate`, `VulkanFaultInjector` with table tests (12, 13); the ci-tier self-test case | this note, G1 (the project references Graphics) | 200 + 60 + 280 + 50 = ~590 |
| G3b feat(graphics-vulkan): decide and preload the Vulkan loader | `IVulkanLoaderHost`, the process host (`setenv` import), `LoaderDecision`, `LoaderSetup`; fake-host tests of every OS branch (2) | G3a | 200 + 60 + 300 = ~560 |
| G3c feat(graphics-vulkan): add the backend's public entry point | `VulkanBackend.Create`/`Name`/`LoaderPath`, `VulkanBackendOptions`, `ValidationAllowance` and their misuse rules (the public surface); the `graphics-vulkan` smoke scenario (14); the ObjectPool pin and lock files if P3 hasn't added them ([ADR-0011](../adr/0011-brutal-objectpool-dependency.md)) | G3b | 150 + 95 + 180 + 150 = ~575 |
| G3d feat(graphics-vulkan): create the instance | Extension and layer enumeration (with the layer's own extension list), instance creation and its chain, disposal (1); the `Gpu`-trait filters in the build-test and vulkan-smoke jobs | G3c | 240 + 40 + 220 + 25 = ~525 |
| G3e feat(graphics-vulkan): count validation messages per instance | `ValidationSink`, the callback, the allow-list (1); the smoke's messenger moved to `pUserData` (14) | G3d | 190 + 50 + 250 + 70 = ~560 |
| G4 feat(graphics-vulkan): select the adapter and create the device | Candidates, `AdapterSelector` with overrides and rejection reasons, queue family choice, the feature chain, extensions, portability restrictions, capabilities, limits and format support, debug names (3); `EnumerateAdapters` | G3e, G2b (`BackendDeviceRequest`, `GraphicsAdapterInfo`) | ~700 |
| G5 feat(graphics-vulkan): present through swapchains with frames in flight | A swapchain component on a raw `VkSurfaceKHR`, `SwapchainPlanner`, creation and recreation, acquire and present with `VkCheck.SwapchainStatusOf`, present fences with the idle fallback (4); frame slots, fences, command pools, submit, the `submitted` rule, the bounded `WaitIdle` (5). Not blocked by P3, unlike the graphics note's plan (`docs/design/graphics.md:1017`): the component takes a raw surface, and its tests make one from a window created through BRUTAL's SDL binding; the `WindowSurfaceSource` path arrives with G9e | G4 | ~750 |
| G6 feat(graphics-vulkan): allocate memory with VMA and track resources | The VMA package version, the allocator, `BackendMemory` placement, persistent mapping, budgets, buffers and textures, the object tables and backend leak report (6); the smoke's VMA step | G5 | ~650 |
| G7 feat(graphics-vulkan): copy buffers and textures, and add views and samplers | Copy commands and the row-pitch mapping (7), texture views, samplers with the portability rule (3) | G6 | ~600 |
| G8 feat(graphics-vulkan): create shader modules, layouts and bind groups | Shader modules, set and pipeline layouts, persistent and frame descriptor pools, bind group writes (8, 9) | G7, T1 (a cooked test shader) | ~650 |
| G9a feat(graphics-vulkan): map pipeline state to Vulkan | The pure mappings: vertex formats, topology, raster, blend, depth, shader stages, the negative-height viewport helper (9) | G8 | 260 + 50 + 260 = ~570 |
| G9b feat(graphics-vulkan): create graphics and compute pipelines | `VkPipelineRenderingCreateInfo`, the pipeline cache, entry-point copies, portability checks, the per-call `InvalidData` override (9) | G9a | 260 + 40 + 250 = ~550 |
| G9c feat(graphics-vulkan): record frames from the command stream | The recorder for every `CommandKind` except `Barrier`: passes, state, draws, dispatches, push constants, debug groups, copies (7, 9, 11's labels) | G9b, G2 (`CommandKind`, `CommandReader`) | 300 + 40 + 260 = ~600 |
| G9d feat(graphics-vulkan): turn planned transitions into barriers | The `ResourceState` table, the acquired-image stage rule, `ClearSwapchainImage`, the host-read barrier, the canary barrier (1, 5, 10); the push-constant matrix round trip (9) | G9c | 180 + 40 + 220 + 150 = ~590 |
| G9e feat(graphics-vulkan): implement the backend device | `VulkanDevice : IBackendDevice` over the components, `CreateSurface` through `WindowSurfaceSource`, `IGraphicsBackend.CreateDevice`, `InjectValidationError`, the hung-GPU dispose rule (5) | G9d | 300 + 60 + 200 = ~560 |
| G9f test(graphics-vulkan): run the backend device end to end | A test `IWindow`/`IVulkanSurfaceSource` over BRUTAL's SDL binding (or Platform.Sdl3 once P3 lands), windowed and headless swapchains through `GraphicsDevice`, the 1,000-frame allocation test, the smoke's full-device scenario (14, 15) | G9e | 80 + 20 + 240 + 80 = ~420 |
| G10 feat(graphics): order passes and insert barriers with a minimal frame graph | The graphics note's planner, plus two rules from this note, proposed to the graphics note ("Points" 12): `ClearSwapchainImage` sits between `ColorTarget` transitions, and upload copies come first (7, 10) | G2 | ~650 |
| G11 feat(graphics-vulkan): measure pass times with timestamps | Query pools and their growth, timestamp writes, `TryReadTimestamps` with the bit mask, debug labels, and offering `TimestampQueries` (11) | G9e | ~450 |
| G12 feat(graphics-vulkan): prove device-loss detection and recovery | Fault injection at every `VulkanCall`, recovery through `Recover` end to end on lavapipe and the RTX (12) | G9f, G10 | ~500 |

Order: G3a to G9f in sequence, then G11 and G12. C1 (the conformance suite) needs a whole backend device, so it waits for G9e and G10, not G7 as the graphics note's split assumed ("Points" 13); until then each issue's GPU cases live in the backend's own `Gpu` tests.

## Decisions made in this note

Small choices the contract left to the backend:

1. M1 records uploads on the graphics queue; the transfer-queue path is designed for M2 (section 7).
2. Clip space is y-up with depth 0 to 1, through negative-height viewports (section 9).
3. The backend fault seam is compiled into release builds, internal (section 12).
4. Host-visible memory is required to be coherent, and readback gets an explicit host-read barrier (sections 5, 6); allocations stay within the heap budget.
5. Swapchain formats are chosen from a fixed list of four, else `Unsupported` (section 4).
6. `VulkanBackendOptions` carries the validation allow-list and the explicit loader path; `LoaderPath` is how the host hands the loader to Platform.Sdl3 (section "The backend's public surface").
7. Backend GPU tests live in the backend's test project under a `Gpu` trait, run by the vulkan-smoke job (section 15).
8. An acquire timeout is reported as `OutOfDate`, not as a loss; `WaitIdle` is bounded by `GpuTimeout` (sections 4, 5).
9. A device whose loss was a GPU timeout is abandoned, never destroyed, consistent with the runtime host note (section 5).
10. The negative canary uses a backend-owned image, so it fires on any frame (section 1).

## Points for the graphics note (reported, not changed)

None blocks an implementation of this note. Each is a clarification or proposal for the issue that owns it (G2b for the front end and contract wording, G10 for the planner):

1. **Acquire timeouts.** `SwapchainStatus` has no value for "no image yet, try later", and the frame-call rule doesn't cover `AcquireImage`. This note reports a timeout as `OutOfDate` (section 4). That costs a recreation and blocks the owner thread for up to two `GpuTimeout`s per frame (the acquire, then the retry: 20 s at the default), and if a compositor that withholds images also delays present-fence signals (unverified), the bounded `WaitIdle`/`DestroySwapchain` path still ends as a `TimedOut` loss. Proposal, to weigh against both: a `SwapchainStatus.Timeout` that the front end maps to `AcquireStatus.Skipped`, with a loss only when the following bounded `WaitIdle` times out, perhaps with a shorter acquire timeout than `GpuTimeout`.
2. `CreateSwapchain` can fail during the acquire-time recreation (a window minimised between the front end's check and the surface query gives `Unavailable`); the status table has no row for it. Mapping it to `Minimised` fits the interface.
3. "Otherwise the first format" (graphics note section 3) can't be honoured: `SwapchainInfo.Format` must be a `TextureFormat` (section 4 here).
4. The interface states no clip-space convention; section 9 chooses one, and the graphics note should state it so every backend matches.
5. `EnumerateAdapters` with a `CompatibleWindow` creates a temporary surface, so it must not run while a device presents to that window (the platform note's one-surface rule).
6. The interface exposes no GPU memory budget; `GraphicsStatistics` could gain one later.
7. The timestamp query count per frame is unbounded in the contract; the backend grows its pools (section 11).
8. **Proposal:** require `VK_KHR_swapchain` only when there is a `CompatibleWindow`, so an offscreen-only device (a server rendering thumbnails, a compute tool) works on an adapter without it. This note follows `docs/design/graphics.md:115` and requires it always (section 3).
9. **Proposal:** add "a queue family with graphics and compute" to the required set, so `CreateComputePipeline` can't fail with `Unsupported` on an accepted adapter. This note follows `docs/design/graphics.md:115` and treats compute as a preference (section 3).
10. **The memory row** (`docs/design/graphics.md:982`) says "(coherent memory, or a flush and invalidate)". That covers CPU writes, not GPU writes the CPU reads: those also need a memory dependency into the host domain (`HOST`/`HOST_READ`) before the fence, whatever the memory type. Proposed wording: "(coherent memory or a flush and invalidate, and for GPU writes a host-read memory dependency before the frame's completion signal)". Section 5 implements it.
11. **Dispose after a hung GPU.** `GraphicsDevice.Dispose` waits for the GPU to go idle (`docs/design/graphics.md:80`), which can block forever after a `TimedOut` loss. The runtime host note (#57) abandons the device instead, and this note's backend refuses to destroy anything in that case (section 5). Proposal: the graphics note states that neither `GraphicsDevice.Dispose` nor `Recover` (which today destroys all native objects, `docs/design/graphics.md:748`) runs after a `TimedOut` loss; this note assumes that and doesn't change the contract.
12. **The planner's `ClearSwapchainImage` rule.** The backend records `ClearSwapchainImage` as a clearing render pass, so it needs the image in `ColorTarget`; graphics note section 10 doesn't say which state the planner puts it in. Proposal: state that the planner surrounds it with `ColorTarget` transitions (G10), and that upload copies come first in the frame.
13. **C1's blocker.** The graphics note's split has C1 wait for "G10, and #55's split up to uploads and textures (G7)" (`docs/design/graphics.md:1013`). A conformance run needs a whole `IBackendDevice`, which arrives in G9e here, so C1 waits for G9e and G10.

## Deviations from the architecture and testing docs

1. **No transfer queue in M1** ([Architecture 3.8](../03-architecture.md#38-graphics-render-interface-brutalenginegraphics-and-vulkan-backend): "a transfer queue when available"), by section 7's decision.
2. **No MoltenVK test runs in M1** (Architecture 3.8: portability restrictions "checked by tests running on MoltenVK"), by the maintainer's macOS decision.
3. **No self-hosted real-GPU nightly** ([Testing strategy](../05-testing-strategy.md#gpu-testing-without-gpus)): local RTX runs per graphics PR, by the real-GPU decision.

## Not verified

- The note is design only; nothing in it has run. M0's proven behaviour is cited where it is ported.
- Cited, not observed by this note: the RTX 2070 SUPER's swapchain maintenance1, timestamp and present-wait support (the maintainer's `vulkaninfo`, 2026-11-26); the y-flip keeping `FrontFace` meaning (from the Vulkan specification; G9c pins it); the specification's guarantee of a host-coherent memory type; `VK_EXT_swapchain_maintenance1` and timestamp support on lavapipe (unknown until G5 and G11 run there).
- The host-read barrier and the hung-GPU abandon rule follow the Vulkan specification's memory model and the runtime host note; neither has run (G9d and G12 test them).
- VMA has never run in this repository: the package isn't restored anywhere yet, and its native library loading under NativeAOT is unproven until G6.
- Everything on macOS: portability enumeration, the bundled loader, `VK_DRIVER_FILES`, MoltenVK and the portability subset.
