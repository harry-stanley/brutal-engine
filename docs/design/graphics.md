# Design: render interface (`BrutalEngine.Graphics`)

## Purpose

`BrutalEngine.Graphics` is the backend-neutral render interface from [Architecture 3.8](../03-architecture.md#38-graphics-render-interface-brutalenginegraphics-and-vulkan-backend) and [ADR-0002](../adr/0002-render-interface-vulkan-first.md): low-level and explicit, at about the level of WebGPU, with no 2D concepts. Vulkan 1.3 (`Graphics.Vulkan`) is the only v1 backend. Its mechanics (instance, VMA, sync2 barriers, descriptor pools, the BRUTAL workarounds) belong to the Vulkan backend note, `docs/design/graphics-vulkan.md` (#55). This note fixes the public API that every backend implements and the conformance suite tests. Merging it approves that API; G1, G2, G10 and C1 are drafted from it (see section 16).

Goals that shape every decision below:

- **No backend type in a public signature.** Every public type here uses only BCL types, Core types and the types this note defines. The architecture tests enforce it ([no binding types](architecture-tests.md#rules)). A later Metal, D3D12 or SDL_GPU backend implements the same interface.
- **Misuse throws, expected failure returns** ([errors](errors.md)). Device loss, out of memory and unsupported formats are values; stale handles and bad descriptions are exceptions.
- **Steady-state frames allocate zero managed bytes** ([Architecture 3.3](../03-architecture.md#33-memory)).
- **Proven behaviour first.** The M0 sample `samples/HelloWindow` already runs frames in flight, acquire, present, resize and minimise on lavapipe, MoltenVK and the RTX 2070 SUPER. The interface keeps its rules and cites them.

Decisions this note depends on (maintainer, 2026-11-26):

- **Window to swapchain:** a documented L2 edge `Graphics -> Platform`. A window exposes a backend-neutral surface capability, `IWindow.VulkanSurface` (an `IVulkanSurfaceSource?`, namespace `BrutalEngine.Platform`), defined in the [platform design note](platform.md#4-vulkan-surface-source). This note references it and doesn't redefine it. The edge row lands with P1.
- **Shader bytecode in M1:** shaders reach the runtime as committed SPIR-V cooked offline by `tools/BrutalEngine.Cook` (Slang), and pipeline layouts are declared in C# in M1. Recorded as ADR-0012 (#52).
- **Device loss:** detected everywhere, it raises a typed event, and the device recreates itself and calls owner-registered recreate callbacks. Fault injection is an internal seam, also visible to the conformance suite (section 12).

## Architecture: a front end over a backend interface

```
 game / Rendering2D / DebugOverlay / Runtime
              │  public API (this note)
 ┌────────────▼─────────────────────────────────────────────┐
 │ BrutalEngine.Graphics: GraphicsDevice (sealed)            │
 │  handle tables · description validation · thread check    │
 │  frame lifecycle · deletion queues · upload ring          │
 │  command stream encoding · frame planner (barriers)       │
 └────────────▲─────────────────────────────────────────────┘
              │  backend interface (BrutalEngine.Graphics.Backend)
 ┌────────────┴───────────┐   ┌──────────────────────────────┐
 │ Graphics.Vulkan (L2)   │   │ FakeGraphicsBackend (tests)  │
 └────────────────────────┘   └──────────────────────────────┘
```

`GraphicsDevice` is one sealed class in `BrutalEngine.Graphics`. It owns everything that doesn't depend on the GPU API: handle validation, description validation, the frame state machine, deletion queues, the command stream and the frame planner. A backend implements the small backend interface (section 16 sketches it) and receives only validated descriptions and planned frames. So:

- every backend gets identical misuse behaviour, and the conformance suite tests one set of rules;
- the front end is unit-tested on every OS without a GPU, against an in-memory fake backend (G2b);
- the frame graph's planning logic is pure and property-tested (G10).

The rejected alternative, an abstract `GraphicsDevice` that each backend subclasses, duplicates validation per backend and can't be tested without a GPU.

## 1. Object model, handles, ownership and destruction

**Every GPU object is a typed generational handle owned by the device**, as docs/03 3.8 says ("all are handles owned by the device"). Handles are non-owning value types ([Architecture 3.3](../03-architecture.md#33-memory), [3.4](../03-architecture.md#34-handles-and-resource-identity)); the device is the only owner.

```csharp
namespace BrutalEngine.Graphics;

// One per object kind: BufferHandle, TextureHandle, TextureViewHandle, SamplerHandle, ShaderModuleHandle,
// BindGroupLayoutHandle, PipelineLayoutHandle, BindGroupHandle, GraphicsPipelineHandle, ComputePipelineHandle,
// SwapchainHandle, DebugLabel. Same shape for each.
public readonly struct TextureHandle : IEquatable<TextureHandle>
{
    internal TextureHandle(Handle value);    // only the device creates handles
    public bool IsNull { get; }               // default(TextureHandle) is null
    public bool Equals(TextureHandle other);
    public override string ToString();        // "Texture(index:generation)", allocates
}
```

- **Typed per kind**, each over a Core `Handle` from a per-kind `HandleAllocator` in the device ([handles](handles.md)), so a buffer handle can't be passed where a texture is expected.
- **Stale handles are detected, never dereferenced.** Every call that takes a handle validates it against its allocator. A stale handle throws `StaleHandleException`, `default` throws `ArgumentException`. A handle from another device is detected only when its index or generation doesn't match there (the documented limit of `Handle`).
- **Destruction is immediate for the caller, deferred for the GPU.** `device.Destroy(handle)` frees the handle at once (every copy is stale from then on) and queues the native object on the deletion queue of the frame being recorded, or of the last submitted frame if no frame is open. The native object is destroyed only when that frame's GPU work has completed (docs/03 3.8 "A resource is destroyed only after the GPU frame that last used it has completed"). So a resource may be used and destroyed in the same frame.
- **Dependent objects:** destroying a texture also destroys its views. Destroying a buffer, view or sampler that a persistent bind group references makes the bind group unusable: binding it throws `StaleHandleException` naming the dead resource. Destroying a bind group layout or pipeline layout doesn't affect pipelines and bind groups already created from it.

```csharp
public sealed class GraphicsDevice : IDisposable
{
    public void Destroy(BufferHandle buffer);        // overload per kind; throws StaleHandleException, ArgumentException
    public void Destroy(TextureHandle texture);       // ... TextureViewHandle, SamplerHandle, ShaderModuleHandle,
                                                      // BindGroupLayoutHandle, PipelineLayoutHandle, BindGroupHandle,
                                                      // GraphicsPipelineHandle, ComputePipelineHandle, SwapchainHandle
    public void Dispose();
}
```

`GraphicsDevice` implements `IDisposable` and is owned by whoever created it (the Runtime host, a test, a sample). `Dispose` waits for the GPU to go idle (ignoring a lost device, because destroying objects stays valid then, `samples/HelloWindow/HelloWindowApp.cs:176-186`), logs every live object as a leak (section 13), destroys everything in reverse creation order, then the backend device. Every member except `Dispose` throws `ObjectDisposedException` afterwards.

## 2. Device creation, adapters and capabilities

```csharp
public sealed class GraphicsDevice
{
    public static Result<GraphicsDevice> Create(IGraphicsBackend backend, GraphicsDeviceOptions options);
    public GraphicsAdapterInfo Adapter { get; }
    public GraphicsCapabilities Capabilities { get; }
    public FormatSupport GetFormatSupport(TextureFormat format);
    public static IReadOnlyList<GraphicsAdapterInfo> EnumerateAdapters(IGraphicsBackend backend, GraphicsDeviceOptions options);
}

public sealed record GraphicsDeviceOptions
{
    public required Logger Logger { get; init; }                 // categories "Graphics", "Graphics.Validation"
    public INativeAllocator Allocator { get; init; } = new HeapAllocator(); // command streams, queues, tracking
    public IWindow? CompatibleWindow { get; init; }               // see section 3; null = offscreen-only device
    public AdapterSelection Adapter { get; init; } = AdapterSelection.Auto;
    public bool Validation { get; init; }                         // the host turns it on in debug builds; tests always
    public int FramesInFlight { get; init; } = 2;                 // 1..3
    public GraphicsFeatures RequiredFeatures { get; init; }       // adapter rejected without them
    public GraphicsFeatures OptionalFeatures { get; init; }       // enabled when present
    public long UploadRingBytesPerFrame { get; init; } = 8 << 20; // section 6
    public TimeSpan GpuTimeout { get; init; } = TimeSpan.FromSeconds(10); // a frame this late is a hung GPU
    public string ApplicationName { get; init; } = "BrutalEngine";
}
```

- `Create` returns a failed `Result` for expected failures: no backend runtime (`ErrorKind.Unavailable`, "Vulkan loader missing"), no suitable adapter (`Unsupported`, "No Vulkan 1.3 GPU found", with every rejection reason in the message), validation requested but the layer missing (`Unavailable`), out of memory. Invalid options (`FramesInFlight` outside 1..3, a null logger, required and optional features overlapping) throw `ArgumentException`.
- `GpuTimeout` bounds every internal fence wait; the M0 sample uses the same 10 s rule, so a hung GPU fails a CI job instead of hanging it (`samples/HelloWindow/FrameRenderer.cs:30-32`). A timeout is treated as device loss (section 12).

**Adapter selection** ports the M0 `DeviceSelector` rules: required capabilities first (for Vulkan: 1.3, swapchain, dynamic rendering, synchronization2, a graphics queue that can present to the compatible window), then preference discrete > integrated > virtual > CPU, ties to the first enumerated, and every rejected candidate logged with its reasons (`samples/HelloWindow/DeviceSelector.cs:15-54`, `:69-76`; docs/03 3.8 "Device selection").

```csharp
public readonly record struct AdapterSelection
{
    public static AdapterSelection Auto { get; }
    public static AdapterSelection ByIndex(int index);
    public static AdapterSelection ByName(string nameSubstring);   // ordinal, case-insensitive
    public static AdapterSelection ByType(AdapterType type);
    public static bool TryParse(string text, out AdapterSelection selection); // "auto", "index:N", "name:<text>", "type:discrete"
}

public enum AdapterType { Other, Discrete, Integrated, Virtual, Cpu }

public sealed record GraphicsAdapterInfo
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required AdapterType Type { get; init; }
    public required string Driver { get; init; }                  // e.g. "NvidiaProprietary", "MesaLlvmpipe", "Moltenvk"
    public required string ApiVersion { get; init; }              // e.g. "Vulkan 1.3.280"
    public required IReadOnlyList<string> RejectionReasons { get; init; } // empty if suitable
}
```

The override comes from the host: Runtime maps the `--gpu <selection>` option and the `graphics.adapter` config key through `AdapterSelection.TryParse` (exact token matching against the listed names, never `Enum.TryParse`, so `type:1` or a comma list is rejected). An override that matches no suitable adapter fails `Create` with `Unsupported` naming the override; it never silently falls back.

**Capabilities and negotiated features.** Optional features are negotiated, never assumed (docs/03 3.8 "Capabilities").

```csharp
[Flags]
public enum GraphicsFeatures : long
{
    None = 0,
    SamplerAnisotropy = 1 << 0,
    TimestampQueries = 1 << 1,         // queue timestampValidBits > 0 (section 11)
    TextureCompressionBC = 1 << 2,     // declared now, formats added with M2's texture importer
    TextureCompressionAstc = 1 << 3,   // declared now, formats added with M2's texture importer
}

public sealed record GraphicsCapabilities
{
    public required GraphicsFeatures Features { get; init; }       // the enabled set: required + available optional
    public required GraphicsLimits Limits { get; init; }
}

public readonly record struct GraphicsLimits
{
    public int MaxTextureDimension2D { get; init; }
    public int MaxTextureArrayLayers { get; init; }
    public int MaxBindGroups { get; init; }
    public int MaxPushConstantBytes { get; init; }
    public int MaxColorTargets { get; init; }
    public long MinUniformBufferOffsetAlignment { get; init; }
    public long MinStorageBufferOffsetAlignment { get; init; }
    public float MaxSamplerAnisotropy { get; init; }
    public SampleCounts ColorSampleCounts { get; init; }            // MSAA: negotiated per format via GetFormatSupport
    public double TimestampPeriodNanoseconds { get; init; }         // 0 without TimestampQueries
}

[Flags]
public enum FormatSupport
{
    None = 0, Sampled = 1, Filterable = 2, ColorTarget = 4, Blendable = 8, DepthStencilTarget = 16,
    Storage = 32, CopySource = 64, CopyDestination = 128, Multisample = 256,
}
```

Each limit is read from the backend; for Vulkan they are `VkPhysicalDeviceLimits` fields BRUTAL binds (`Brutal.Vulkan/Generated/Structs/VkPhysicalDeviceLimits.gen.cs:177` maxPushConstantsSize, `:207` maxBoundDescriptorSets, `:537` minUniformBufferOffsetAlignment, `:609` framebufferColorSampleCounts, `:633` maxColorAttachments, `:681` timestampPeriod) and the features from `VkPhysicalDeviceFeatures.gen.cs:192` (samplerAnisotropy), `:210` (textureCompressionBC), `:204` (textureCompressionASTC_LDR). Using a feature that isn't in `Capabilities.Features` throws `InvalidOperationException`: the caller could have checked. MSAA and compressed formats are only declared in M1: how they negotiate is fixed here (`SampleCounts`, `FormatSupport.Multisample`, the compression features), and the formats and resolve attachments come later.

## 3. Swapchains

By the window-to-swapchain decision above, a swapchain is created from a Platform window:

```csharp
public sealed class GraphicsDevice
{
    public Result<SwapchainHandle> CreateSwapchain(IWindow window, SwapchainOptions options);
    public SwapchainInfo GetSwapchainInfo(SwapchainHandle swapchain);
}

public sealed record SwapchainOptions
{
    public PresentMode PresentMode { get; init; } = PresentMode.Fifo;
    public SwapchainFormat Format { get; init; } = SwapchainFormat.Srgb;
    public string? DebugName { get; init; }
}

public enum PresentMode { Fifo, FifoRelaxed, Mailbox, Immediate }   // Fifo: vsync, always supported
public enum SwapchainFormat { Srgb, Unorm }                           // 8-bit BGRA/RGBA, SDR only in M1

public readonly record struct SwapchainInfo(
    int Width, int Height, TextureFormat Format, PresentMode PresentMode, int ImageCount, bool SupportsCopyDestination);
```

- **The surface comes from the window.** The backend obtains the window's `IVulkanSurfaceSource` as specified in `docs/design/platform.md` (#53): required instance extensions, then `CreateVulkanSurface(nint instance)`, which returns the `VkSurfaceKHR` as `nint` through `Result<T>`. A window that can't present (for example headless on Windows, where no driver offers `VK_EXT_headless_surface`, `samples/HelloWindow/VulkanContext.cs:107-112`) reports that through the same contract, and `CreateSwapchain` returns its error unchanged. Nothing Vulkan-typed crosses this API: `IWindow` in, a handle out.
- **Instance extensions are known at device creation.** A Vulkan instance must enable surface extensions when it is created, and adapter selection must check present support for a real surface (`VulkanContext.cs:57-58`, `:262`). So `GraphicsDeviceOptions.CompatibleWindow` names the window the device will present to (WebGPU's "compatible surface"). The surface created for selection is kept and reused by the first `CreateSwapchain` for that window. A device created with no compatible window is offscreen-only: `CreateSwapchain` returns `Unsupported`. Another window works if its required extensions were enabled and the device's queue can present to it; otherwise `Unsupported`.
- **Ownership and order.** The device owns the swapchain and the surface. `Destroy(SwapchainHandle)` waits for the swapchain's presents, then destroys it and the surface (`vkDestroySurfaceKHR`, which the platform contract assigns to the graphics backend). The owner must destroy the swapchain before the window; the Runtime host does it in reverse creation order. `GraphicsDevice.Dispose` destroys any swapchain still alive, logged as a leak.
- **Present mode:** `Fifo` by default, the only mode every implementation must support (`samples/HelloWindow/SwapchainPlanner.cs:9-10`; BRUTAL binds all four, `Brutal.Vulkan/Generated/Enums/VkPresentModeKHR.gen.cs:35`, `:40`, `:45`, `:50`). An unsupported request falls back to `Fifo`, is logged once, and `SwapchainInfo.PresentMode` reports the mode in use.
- **Format, image count, extent, composite alpha** follow the M0 planner: sRGB BGRA if offered, otherwise the first format; one image more than the minimum; the window's pixel size when the surface's current extent is undefined (headless, Wayland), checked for zero before clamping; opaque alpha (`SwapchainPlanner.cs:17-37`, `:43-48`, `:54-70`, `:76-86`).

**Acquisition and recreation are observable results, not exceptions** (docs/03 3.5 "Recoverable GPU events"). Images are acquired inside a frame (section 4):

```csharp
public readonly struct CommandList
{
    public AcquireStatus AcquireSwapchainImage(SwapchainHandle swapchain, out SwapchainImage image);
}

public enum AcquireStatus
{
    Acquired,      // image valid for this frame
    Recreated,     // acquired after recreating the swapchain: size or format changed, resize size-dependent targets
    Minimised,     // zero-size window: no image, render nothing to it (the host waits for events)
    Skipped,       // still out of date after one recreation: no image this frame, try again next frame
    SurfaceLost,   // destroy and recreate the swapchain (rare: the window's surface is gone)
    DeviceLost,    // section 12
}

public readonly record struct SwapchainImage(TextureHandle Texture, TextureViewHandle View, int Width, int Height, TextureFormat Format);
```

Rules, all from the M0 frame loop:

- Each acquire compares the window's pixel size (from `IWindow`, as platform.md defines it) with the swapchain's extent and recreates only when it really changed: SDL reports a resize right after window creation (`HelloWindowApp.cs:137-138`). A zero size or a minimised window returns `Minimised` without recreating (`HelloWindowApp.cs:125-135`; `SwapchainPlanner.cs:26-30`).
- An out-of-date acquire recreates and retries once. Suboptimal or out-of-date presents mark the swapchain for recreation at the next acquire (`samples/HelloWindow/FrameResultPolicy.cs:14-24`).
- The acquired image's view is valid only in this frame. It may be a color target or, if `SupportsCopyDestination`, a copy destination. The frame planner moves it to the present state at submit and presents it then (section 10). An acquired image that no pass wrote is cleared to black before present, so the presentation engine never shows uninitialised memory.
- Recreation waits for the device to go idle in M1, as M0 does (`HelloWindowApp.cs:141`). Present fences with `VK_EXT_swapchain_maintenance1` are a backend optimisation (#55) and don't change this API.

## 4. Frame lifecycle

One frame is open at a time. Its commands are recorded into a `CommandList`, a small value tied to the device's open frame:

```csharp
public sealed class GraphicsDevice
{
    public FrameStatus BeginFrame(out CommandList commands);   // waits for the frame slot's previous GPU work
    public FrameStatus SubmitFrame(CommandList commands);      // plans, submits and presents
    public long FrameNumber { get; }                           // frames submitted so far
    public long CompletedFrameNumber { get; }                  // newest frame whose GPU work has completed
    public Result WaitIdle();                                  // tests, teardown, recreation: never in steady frames
}

public enum FrameStatus { Ready, DeviceLost }
```

- **Frames in flight:** `FramesInFlight` (default 2, docs/03 3.8) slots, each with its own GPU completion fence, command recording memory, upload ring slice and deletion queue. `BeginFrame` waits for slot `FrameNumber % FramesInFlight`'s previous use, then retires that slot's deletion queue and resets its ring. This is the proven M0 baseline: 2 frames in flight, per-frame fence, semaphore and command buffer, and render-finished semaphores per swapchain image (`FrameRenderer.cs:8-14`, `:25`; `Swapchain.cs:5-9`). The per-image semaphores are backend detail; the interface only promises that a frame never overwrites an image the presentation engine still reads.
- **A frame is begun, recorded and submitted, always in that order.** `BeginFrame` with a frame already open, or `SubmitFrame` with a stale or foreign `CommandList`, throws `InvalidOperationException`. A `CommandList` copy used after its frame was submitted throws `InvalidOperationException` (each call checks its frame number against the open frame). There is no abandon: a host that wants to skip rendering submits an empty frame, which still retires deletion queues.
- **A frame always completes.** A failed acquire (`Minimised`, `Skipped`) doesn't stop the frame: the host may still render offscreen work, or submit it empty. The M0 sample resets its fence only once a submit is certain (`FrameRenderer.cs:11-13`); the front end does the same, so no failure path leaves a slot waiting forever.
- `CompletedFrameNumber` advances when `BeginFrame` observes completed fences (and on `WaitIdle`). It drives readback (section 6) and timestamp results (section 11).
