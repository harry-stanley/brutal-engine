# Design: render interface (`BrutalEngine.Graphics`)

## Purpose

`BrutalEngine.Graphics` is the backend-neutral render interface from [Architecture 3.8](../03-architecture.md#38-graphics-render-interface-brutalenginegraphics-and-vulkan-backend) and [ADR-0002](../adr/0002-render-interface-vulkan-first.md): low-level and explicit, at about the level of WebGPU, with no 2D concepts. Vulkan 1.3 (`Graphics.Vulkan`) is the only v1 backend. Its mechanics (instance, VMA, sync2 barriers, descriptor pools, the BRUTAL workarounds) belong to the Vulkan backend note, `docs/design/graphics-vulkan.md` (#55). This note fixes the public API that every backend implements and the conformance suite tests. Merging it approves that API, including the backend interface; G1, G2, G2b, G10 and C1 are drafted from it (see section 16).

Goals that shape every decision below:

- **No backend type in a public signature.** Every public type here uses only BCL types, Core types and the types this note defines. The architecture tests enforce it ([no binding types](architecture-tests.md#rules)). A later Metal, D3D12 or SDL_GPU backend implements the same interface.
- **Misuse throws, expected failure returns** ([errors](errors.md)). Device loss, out of memory and unsupported formats are values; stale handles and bad descriptions are exceptions.
- **Steady-state frames allocate zero managed bytes** ([Architecture 3.3](../03-architecture.md#33-memory)).
- **Proven behaviour first.** The M0 sample `samples/HelloWindow` already runs frames in flight, acquire, present, resize and minimise on lavapipe, MoltenVK and the RTX 2070 SUPER. The interface keeps its rules and cites them.

Decisions this note depends on (maintainer, 2026-11-26):

- **Window to swapchain:** a documented L2 edge `Graphics -> Platform`. A window exposes a backend-neutral surface capability, `IWindow.VulkanSurface` (an `IVulkanSurfaceSource?`, namespace `BrutalEngine.Platform`), defined in the [platform design note](platform.md#4-vulkan-surface-source). This note references it and doesn't redefine it. The edge row lands with P1.
- **Shader bytecode in M1:** shaders reach the runtime as committed SPIR-V cooked offline by `tools/BrutalEngine.Cook` (Slang), and pipeline layouts are declared in C# in M1. Recorded as [ADR-0012](../adr/0012-m1-shader-cooking.md).
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

`GraphicsDevice` is one sealed class in `BrutalEngine.Graphics`. It owns everything that doesn't depend on the GPU API: handle validation, description validation, the frame state machine, deletion queues, the command stream and the frame planner. A backend implements the backend interface (section "The backend interface") and receives only validated descriptions and planned frames. So:

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

`GraphicsDevice` implements `IDisposable` and is owned by whoever created it (the Runtime host, a test, a sample). `Dispose` waits for the GPU to go idle (ignoring a lost device, because destroying objects stays valid then, `samples/HelloWindow/HelloWindowApp.cs:225-235`), logs every live object as a leak (section 13), destroys everything in reverse creation order, then the backend device. Every member except `Dispose`, `Adapter` and `Capabilities` throws `ObjectDisposedException` afterwards; those two keep returning the values read at creation (section 14).

## 2. Device creation, adapters and capabilities

```csharp
public sealed class GraphicsDevice
{
    public static Result<GraphicsDevice> Create(IGraphicsBackend backend, GraphicsDeviceOptions options);
    public GraphicsAdapterInfo Adapter { get; }
    public GraphicsCapabilities Capabilities { get; }
    public FormatSupport GetFormatSupport(TextureFormat format);
    public static Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(IGraphicsBackend backend, GraphicsDeviceOptions options);
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
- `EnumerateAdapters` fails the same way as `Create` when there is no backend runtime. A suitable-or-not list, with `RejectionReasons`, is a success. With a `CompatibleWindow` the backend creates a temporary surface for that window, so `EnumerateAdapters` must not be called while any device presents to it; nothing checks this, because devices don't know each other.
- **The backend object belongs to the host.** `IGraphicsBackend` is stateless (section "The backend interface"): `Create` neither takes ownership of it nor disposes it, whether it succeeds or fails. Every native object lives in the `IBackendDevice` the device creates and disposes.
- `GpuTimeout` bounds every internal fence wait; the M0 sample uses the same 10 s rule, so a hung GPU fails a CI job instead of hanging it (`samples/HelloWindow/FrameRenderer.cs:32-34`). A timeout is treated as device loss (section 12).

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

[Flags] public enum SampleCounts { None = 0, X1 = 1, X2 = 2, X4 = 4, X8 = 8, X16 = 16 }

[Flags]
public enum FormatSupport
{
    None = 0, Sampled = 1, Filterable = 2, ColorTarget = 4, Blendable = 8, DepthStencilTarget = 16,
    Storage = 32, CopySource = 64, CopyDestination = 128, Multisample = 256,
}
```

Each limit is read from the backend; for Vulkan they are `VkPhysicalDeviceLimits` fields BRUTAL binds (`Brutal.Vulkan/Generated/Structs/VkPhysicalDeviceLimits.gen.cs:177` maxPushConstantsSize, `:207` maxBoundDescriptorSets, `:537` minUniformBufferOffsetAlignment, `:609` framebufferColorSampleCounts, `:633` maxColorAttachments, `:681` timestampPeriod) and the features from `VkPhysicalDeviceFeatures.gen.cs:192` (samplerAnisotropy), `:210` (textureCompressionBC), `:204` (textureCompressionASTC_LDR). Using a feature that isn't in `Capabilities.Features` throws `InvalidOperationException`: the caller could have checked. MSAA and compressed formats are only declared in M1: how they negotiate is fixed here (`SampleCounts`, `FormatSupport.Multisample`, the compression features), and the formats and resolve attachments come later.

## 3. Swapchains

A swapchain is created from a Platform window, through the capability the [platform note](platform.md#4-vulkan-surface-source) defines:

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

- **The surface comes from the window.** `IWindow.VulkanSurface` (an `IVulkanSurfaceSource?`, [platform note section 4](platform.md#4-vulkan-surface-source)) gives the required instance extensions (`RequiredInstanceExtensionCount`, `GetRequiredInstanceExtension(int)`: UTF-8 spans) and `CreateVulkanSurface(nint instance)`, which returns the `VkSurfaceKHR` as `nint` through `Result<nint>`. Nothing Vulkan-typed crosses this API: `IWindow` in, a handle out.
- **A window without a surface is an expected failure.** `VulkanSurface` is `null` for a window created with `WindowSurface.None` (which the `dummy` driver requires). `CreateSwapchain` for such a window returns a failed `Result` with `ErrorKind.Unsupported` ("window was created with WindowSurface.None"), and so does `GraphicsDevice.Create` when it is the `CompatibleWindow`. They are `Result`s, not exceptions ([errors](errors.md)), because a surfaceless window is a legitimate configuration (`NoSurface` headless hosts, servers) that a caller may probe. A failed `CreateVulkanSurface` is returned unchanged (`ErrorKind.Native`, SDL's message). A headless window that could never present (`Offscreen` without `VK_EXT_headless_surface`, as on Windows) already fails at `IPlatform.CreateWindow` ([platform note section 3](platform.md#3-headless-mode)), so Graphics never receives one.
- **The backend never sees the window.** The layer map allows a backend exactly one same-layer edge, to the abstraction it implements (`tests/BrutalEngine.Architecture.Tests/LayerMap.cs:108-115`), and the layering rule checks direct references (`tests/BrutalEngine.Architecture.Tests/LayeringRule.cs:65`). So Graphics.Vulkan never names `IWindow` or `IVulkanSurfaceSource`. `GraphicsDevice` wraps the window's capability in a Graphics-owned type with only `int`, `bool`, `nint`, spans and `Result` in its public surface, and passes that to the backend (in the device request for the `CompatibleWindow`, and in each swapchain request):

```csharp
namespace BrutalEngine.Graphics.Backend;

public sealed class WindowSurfaceSource
{
    internal WindowSurfaceSource(IWindow window, IVulkanSurfaceSource source); // only GraphicsDevice creates one
    public int RequiredInstanceExtensionCount { get; }                       // forwards to the window's source
    public ReadOnlySpan<byte> GetRequiredInstanceExtension(int index);       // UTF-8, no NUL; any thread
    public Result<nint> CreateVulkanSurface(nint instance);                  // main thread; the backend owns the surface
    public int PixelWidth { get; }                                           // IWindow.PixelSize, refreshed by the front end
    public int PixelHeight { get; }                                          //   before every backend call that uses it
    public bool IsMinimised { get; }                                         // WindowState.Minimized, or a zero side
}
```

  The front end creates one per window it presents to, keeps it while that window's swapchain lives, and refreshes the size fields from `IWindow.PixelSize` and `IWindow.State` (snapshots that cost no native call, platform note section 1) before each acquire. The forwarding members follow the platform note's rules (thread, disposal, argument checks), so a disposed window still surfaces as `ObjectDisposedException`.
- **Instance extensions are known at device creation.** A Vulkan instance must enable surface extensions when it is created, and adapter selection must check present support for a real surface (`VulkanContext.cs:57-58`, `:262`). So `GraphicsDeviceOptions.CompatibleWindow` names the window the device will present to (WebGPU's "compatible surface"). The backend reads the instance extensions from its `WindowSurfaceSource` before creating the instance; the platform note guarantees they are the same for every window of one platform. The surface created for selection is kept and reused by the first `CreateSwapchain` for that window (the device keeps the request's `WindowSurfaceSource` instance, which the backend matches by reference). Until then the device owns it, so the `CompatibleWindow` must outlive the device: the host disposes the device first, the reverse of creation order (platform note section 4, "Ownership"). A device created with no compatible window is offscreen-only: `CreateSwapchain` returns `Unsupported`. Another window works if its required extensions were enabled and the device's queue can present to it; otherwise `Unsupported`.
- **One swapchain per window.** The platform note allows one live surface per window, so `CreateSwapchain` for a window that already has a live swapchain throws `InvalidOperationException` (misuse: the caller holds that swapchain's handle). After `Destroy`, the window's old swapchain and surface wait for a frame (below), so a new `CreateSwapchain` for that window first waits for the GPU to go idle (`WaitIdle`, returning its failure); if they wait for a frame that hasn't been submitted (destroyed in the open frame, or between frames while writes are pending), it throws `InvalidOperationException`: submit that frame first.
- **Creation.** Options outside their enums throw `ArgumentOutOfRangeException`. A window that is minimised or has a zero side at the call returns `ErrorKind.Cancelled` without creating anything (a zero-sized swapchain is invalid; create it once the window is restored). Otherwise the front end creates the surface, then the swapchain with `OldSwapchain = 0`; a surface failure is returned unchanged. **A failed first creation** destroys the surface it was given with `DestroySurface`, the compatible window's selection surface included, and returns the backend's failure unchanged, so a retry never makes a second live surface for the window (it gets a new one). Each image's texture and view ids from `GetSwapchainImage` become a texture handle (with the swapchain's format and size, `ColorTarget` usage plus `CopyDestination` when `SupportsCopyDestination`) whose default view is the image's view. Both belong to the swapchain: they never count in `GraphicsStatistics.LiveObjects` or the leak report, `Destroy` of either and `CreateTextureView` of the texture throw `InvalidOperationException`, and they go stale with the swapchain.
- **Ownership and order.** The device owns the swapchain and the surface. `Destroy(SwapchainHandle)` makes the handle and its images' handles stale at once and, like any object (section 1), queues the swapchain on the deletion queue of the frame that may still use it; once that frame has completed, the backend swapchain is destroyed, then its surface (`vkDestroySurfaceKHR`, which the platform contract assigns to the graphics backend). Before the first frame both go at once. The owner must destroy the swapchain before the window; the Runtime host does it in reverse creation order. A live swapchain counts in `GraphicsStatistics.LiveObjects` and reads `Swapchain 'DebugName'` in the leak report. `GraphicsDevice.Dispose` destroys any swapchain still alive, each before its surface, after logging it as a leak.
- **Present mode:** `Fifo` by default, the only mode every implementation must support (`samples/HelloWindow/SwapchainPlanner.cs:9-10`; BRUTAL binds all four, `Brutal.Vulkan/Generated/Enums/VkPresentModeKHR.gen.cs:35`, `:40`, `:45`, `:50`). An unsupported request falls back to `Fifo`, is logged once, and `SwapchainInfo.PresentMode` reports the mode in use.
- **Format, image count, extent, composite alpha** follow the M0 planner, except the format: a fixed preference list per `SwapchainFormat` (sRGB or UNORM, BGRA before RGBA, the other encoding last; [Vulkan backend note section 4](graphics-vulkan.md#4-swapchains-and-presentation)), and a surface that offers none of them is `Unsupported`, because `SwapchainInfo.Format` is a `TextureFormat` and an arbitrary surface format may have none; one image more than the minimum; the window's pixel size when the surface's current extent is undefined (headless, Wayland), checked for zero before clamping; opaque alpha (`SwapchainPlanner.cs:17-37`, `:58-63`, `:69-85`, `:91-101`).

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

- Each acquire compares `IWindow.PixelSize` with the swapchain's extent and recreates only when it really changed: SDL reports a resize right after window creation (`HelloWindowApp.cs:154-155`). A zero side or `WindowState.Minimized` returns `Minimised` without recreating (`HelloWindowApp.cs:142-152`; `SwapchainPlanner.cs:26-30`).
- An out-of-date acquire recreates and retries once. Suboptimal or out-of-date presents mark the swapchain for recreation at the next acquire (`samples/HelloWindow/FrameResultPolicy.cs:14-24`).
- The acquired image's view is valid only in this frame. It may be a color target or, if `SupportsCopyDestination`, a copy destination. The frame planner moves it to the present state at submit and presents it then (section 10). An acquired image that no pass wrote is cleared to black before present, so the presentation engine never shows uninitialised memory.
- Recreation waits for the device to go idle in M1, as M0 does (`HelloWindowApp.cs:158`). Present fences with `VK_EXT_swapchain_maintenance1` are a backend optimisation (#55) and don't change this API.

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

- **Frames in flight:** `FramesInFlight` (default 2, docs/03 3.8) slots, each with its own GPU completion fence, command recording memory, upload ring slice and deletion queue. `BeginFrame` waits for slot `FrameNumber % FramesInFlight`'s previous use, then retires that slot's deletion queue and resets its ring. This is the proven M0 baseline: 2 frames in flight, per-frame fence, semaphore and command buffer, and render-finished semaphores per swapchain image (`FrameRenderer.cs:13-16`, `:27`; `Swapchain.cs:5-9`). The per-image semaphores are backend detail; the interface only promises that a frame never overwrites an image the presentation engine still reads.
- **A frame is begun, recorded and submitted, always in that order.** `BeginFrame` with a frame already open, or `SubmitFrame` with a stale or foreign `CommandList`, throws `InvalidOperationException`. A `CommandList` copy used after its frame was submitted throws `InvalidOperationException` (each call checks its frame number against the open frame). There is no abandon: a host that wants to skip rendering submits an empty frame, which still retires deletion queues.
- **A frame always completes.** A failed acquire (`Minimised`, `Skipped`) doesn't stop the frame: the host may still render offscreen work, or submit it empty. No failure path leaves a slot waiting forever: the backend contract's frame-call rule (section "The backend interface") requires it.
- `CompletedFrameNumber` advances when `BeginFrame` observes completed fences (and on `WaitIdle`). It drives readback (section 6) and timestamp results (section 11).

## 5. Resource descriptions

Descriptions are immutable records validated by the front end before the backend sees them. Every one has an optional `DebugName` (section 13).

```csharp
public sealed class GraphicsDevice
{
    public Result<BufferHandle> CreateBuffer(in BufferDescription description);
    public Result<TextureHandle> CreateTexture(in TextureDescription description);
    public TextureViewHandle GetDefaultView(TextureHandle texture);          // created with the texture
    public Result<TextureViewHandle> CreateTextureView(in TextureViewDescription description);
    public Result<SamplerHandle> CreateSampler(in SamplerDescription description);
}

public readonly record struct BufferDescription
{
    public required long Size { get; init; }                     // > 0
    public required BufferUsage Usage { get; init; }             // != None
    public MemoryIntent Memory { get; init; }                    // default GpuOnly
    public string? DebugName { get; init; }
}

[Flags] public enum BufferUsage { None = 0, Vertex = 1, Index = 2, Uniform = 4, Storage = 8, Indirect = 16, CopySource = 32, CopyDestination = 64 }

public enum MemoryIntent
{
    GpuOnly,    // device-local; written through the upload ring (section 6)
    Upload,     // rewritten often from the CPU: a placement hint (host-visible device memory when available);
                // same WriteBuffer semantics as GpuOnly
    Readback,   // host-visible, cached: the destination of GPU-to-CPU copies; only CopyDestination usage
}

public readonly record struct TextureDescription
{
    public required TextureFormat Format { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public int ArrayLayers { get; init; } = 1;
    public int MipLevels { get; init; } = 1;                     // M1 content uses 1; mips are supported by the API
    public int SampleCount { get; init; } = 1;                   // > 1 needs FormatSupport.Multisample
    public required TextureUsage Usage { get; init; }
    public string? DebugName { get; init; }
}

[Flags] public enum TextureUsage { None = 0, Sampled = 1, Storage = 2, ColorTarget = 4, DepthStencilTarget = 8, CopySource = 16, CopyDestination = 32 }

public enum TextureFormat
{
    Undefined, R8Unorm, Rg8Unorm, Rgba8Unorm, Rgba8UnormSrgb, Bgra8Unorm, Bgra8UnormSrgb,
    R16Float, Rg16Float, Rgba16Float, R32Float, Rg32Float, Rgba32Float, R32Uint, Rgba8Uint,
    Depth16Unorm, Depth32Float,
}

public readonly record struct TextureViewDescription
{
    public required TextureHandle Texture { get; init; }
    public TextureViewDimension Dimension { get; init; }
    public int BaseMipLevel { get; init; }
    public int MipLevelCount { get; init; } = 1;
    public int BaseArrayLayer { get; init; }
    public int ArrayLayerCount { get; init; } = 1;
    public string? DebugName { get; init; }
}

public readonly record struct SamplerDescription
{
    public Filter MinFilter { get; init; }                       // Nearest by default: pixel art
    public Filter MagFilter { get; init; }
    public Filter MipmapFilter { get; init; }
    public AddressMode AddressU { get; init; }
    public AddressMode AddressV { get; init; }
    public BorderColor BorderColor { get; init; }                // with ClampToBorder
    public float MaxAnisotropy { get; init; } = 1;               // > 1 needs GraphicsFeatures.SamplerAnisotropy
    public CompareFunction? Compare { get; init; }               // depth-compare samplers
    public string? DebugName { get; init; }
}

public enum TextureViewDimension { Texture2D, Texture2DArray }
public enum Filter { Nearest, Linear }
public enum AddressMode { ClampToEdge, Repeat, MirrorRepeat, ClampToBorder }
public enum BorderColor { TransparentBlack, OpaqueBlack, OpaqueWhite }
```

The texture formats are the set every M1 consumer needs (sprites, render targets, the overlay, conformance), plus the two depth formats. `Depth16Unorm` is guaranteed as a depth attachment by the Vulkan specification; `Depth32Float` is near-universal but checked like any other format. Depth-stencil formats (stencil masking for UI, M3), 3D and cube textures are added when a consumer needs them. Nearest filtering is the default because pixel-art is the common 2D case.

A texture's default view (`GetDefaultView`) covers every mip level and layer and lives exactly as long as its texture: `Destroy(TextureViewHandle)` on it throws `InvalidOperationException` and destroys nothing, and destroying the texture destroys it along with every view `CreateTextureView` made.

**Which failures are exceptions and which are results:**

| Failure | Mechanism |
|---------|-----------|
| Structurally invalid description: zero size or usage, mip count above `floor(log2(max(w,h)))+1`, a view outside its texture, `Readback` with a non-copy usage, a non-positive `MaxAnisotropy` | `ArgumentException` (`ArgumentOutOfRangeException` for ranges) |
| A limit exceeded (`Width > MaxTextureDimension2D`) or a feature not enabled (`MaxAnisotropy > 1` without `SamplerAnisotropy`) | `ArgumentOutOfRangeException` / `InvalidOperationException`: the caller can check `Capabilities` |
| Format, usage or sample count the device doesn't support (`GetFormatSupport` says no) | `Result` failure, `ErrorKind.Unsupported`: depends on hardware, so callers fall back |
| Device or host out of memory | `Result` failure, `ErrorKind.OutOfMemory` |
| Device lost | `Result` failure, `ErrorKind.Unavailable` (section 12) |

## 6. Uploads, transient data and readback

All CPU-to-GPU data goes through a per-frame **upload ring** (docs/03 3.8 "per-frame upload rings", "a staging ring"):

```csharp
public sealed class GraphicsDevice
{
    public Result WriteBuffer(BufferHandle buffer, long offset, ReadOnlySpan<byte> data);
    public Result WriteTexture(in TextureWrite write, ReadOnlySpan<byte> data);
    public bool TryReadBuffer(BufferHandle buffer, long offset, Span<byte> destination);
}

public readonly record struct TextureRegion(int X, int Y, int Width, int Height);   // texels; Width, Height > 0

public readonly record struct TextureLocation(TextureHandle Texture, int MipLevel = 0, int ArrayLayer = 0, TextureAspect Aspect = TextureAspect.Color);
public enum TextureAspect { Color, Depth }

public readonly record struct TextureWrite
{
    public required TextureLocation Target { get; init; }       // Color aspect only
    public required TextureRegion Region { get; init; }
    public int BytesPerRow { get; init; }                        // of data; 0 = Width * texel size
}

public readonly record struct BufferCopy
{
    public required BufferHandle Source { get; init; }
    public long SourceOffset { get; init; }
    public required BufferHandle Destination { get; init; }
    public long DestinationOffset { get; init; }
    public required long Size { get; init; }
}

public readonly record struct TextureBufferCopy        // both directions
{
    public required TextureLocation Texture { get; init; }
    public required TextureRegion Region { get; init; }
    public required BufferHandle Buffer { get; init; }
    public long BufferOffset { get; init; }
    public required int BytesPerRow { get; init; }               // multiple of RowPitchAlignment
    public const int RowPitchAlignment = 256;
}

public readonly record struct TextureCopy
{
    public required TextureLocation Source { get; init; }
    public required TextureRegion SourceRegion { get; init; }
    public required TextureLocation Destination { get; init; }
    public int DestinationX { get; init; }
    public int DestinationY { get; init; }
}

public readonly struct CommandList
{
    public Result AllocateTransient(long byteCount, TransientUsage usage, out TransientAllocation allocation);
    public void CopyBufferToBuffer(in BufferCopy copy);
    public void CopyTextureToBuffer(in TextureBufferCopy copy);
    public void CopyBufferToTexture(in TextureBufferCopy copy);
    public void CopyTextureToTexture(in TextureCopy copy);      // same format and size; no scaling in M1
}

public readonly ref struct TransientAllocation
{
    public BufferHandle Buffer { get; }      // a per-frame alias of the slot's ring buffer: stale after the frame
    public long Offset { get; }              // aligned for the usage (uniform: MinUniformBufferOffsetAlignment)
    public Span<byte> Data { get; }          // CPU-writable until SubmitFrame
}

[Flags] public enum TransientUsage { Vertex = 1, Index = 2, Uniform = 4, Storage = 8, Indirect = 16 }
```

- **Write semantics (the same as WebGPU's `queue.writeBuffer`):** `WriteBuffer` and `WriteTexture` copy `data` into the ring at once, so the caller's span can be reused when the call returns. The GPU copies run at the start of the next submitted frame, before any of its passes, and the frame planner orders them after every earlier frame's use of the destination (section 10). So a write is visible to every pass of the next frame, never to work already submitted, and never races a frame in flight. Inside a frame, writes go into the open slot's ring, which `BeginFrame` freed by waiting for that slot. Writes made between frames (loading) can't use a ring slot, which may still be in flight, so they go into pending upload blocks: host-visible staging buffers from a small reusable pool, copied at the start of the next submitted frame and returned to the pool when that frame completes. `GraphicsStatistics.UploadBytesLastFrame` counts both. To give two passes of one frame different data, use two buffers or transient allocations.
- **Transient allocations** are for data rebuilt every frame (sprite instances, overlay vertices, per-frame uniforms): the CPU writes straight into ring memory that the GPU reads in the same frame, without a copy. They can be bound with an offset as vertex, index or indirect buffers, or in a frame bind group (section 8). Using one after its frame was submitted is a stale handle.
- **Alias handles.** A transient allocation's `Buffer` is an alias of its block (the slot's ring, or an overflow block), one per block, made the first time a frame allocates from it. It reports every transient usage plus `CopySource`, never `CopyDestination`, so it can be bound and copied from but never written or copied into. `Destroy` of an alias throws `ArgumentException`; aliases are left out of `GraphicsStatistics.LiveObjects` and the leak report, and every alias goes stale when its frame is submitted, on a lost device too. On a lost device `AllocateTransient` returns `ErrorKind.Unavailable`.
- **Copies.** `CommandList`'s four copies check their handles first (null: `ArgumentException`; not live: `StaleHandleException`), then the copy rules below against each resource's own description, and only then encode the copy into the open frame's command stream with the resources' backend ids. They run in call order with the frame's other commands, after its writes. An invalid copy throws before anything is recorded. On a lost device a copy is still checked, then records nothing (section 12).
- **Overflow:** the ring holds `UploadRingBytesPerFrame` per slot. A write or allocation that doesn't fit gets a dedicated block for that frame only, freed with the frame's deletion queue, counted in `GraphicsStatistics.UploadOverflowBytes` and logged at most once per frame at `Warning`. If that block can't be allocated the call returns `ErrorKind.OutOfMemory` and nothing was written. Overflow costs one GPU allocation; it never stalls or drops data.
- **Readback** (needed by conformance): copy into a `Readback` buffer in a frame (`CopyBufferToBuffer`, `CopyTextureToBuffer`) or `WriteBuffer` into it, then `TryReadBuffer` returns `false` until `CompletedFrameNumber` reaches the last frame that writes it: a copy's frame, or the frame that lands a write (the open one, or the next submitted one between frames). Once it has, it copies the bytes from the buffer's mapped memory. A buffer nothing has written yet reads `true`, with its memory as it is. It never waits and never calls the backend, so a pending read turns `true` only when `BeginFrame` or `WaitIdle` advances `CompletedFrameNumber` (a later copy, `WriteBuffer` or device loss also changes the result); tests that need the result now call `WaitIdle()` first. On a lost device it returns `false`. A non-`Readback` buffer throws `ArgumentException`, and an empty read or one outside the buffer `ArgumentOutOfRangeException`.
- **Copy rules** (each violation throws `ArgumentException`; `ArgumentOutOfRangeException` for ranges):

| Rule | Applies to |
|------|-----------|
| The source has `CopySource` usage and the destination `CopyDestination` | every copy and write |
| Buffer ranges lie inside the buffer; `BufferCopy` offsets and `Size` are multiples of 4; a buffer can't copy onto an overlapping range of itself | `BufferCopy`, `WriteBuffer` |
| `MipLevel < MipLevels`, `ArrayLayer < ArrayLayers`; the region lies inside the mip level's extent, `max(1, size >> mip)`; one layer per copy; the texture has sample count 1 (Vulkan copies only single-sampled images to and from buffers) | every texture location |
| `Aspect` is `Color` for color formats and `Depth` for `Depth16Unorm`/`Depth32Float`. Depth can be copied to buffers but not from them. This is a deliberate simplification: WebGPU forbids buffer-to-depth copies for `Depth32Float`, because Vulkan leaves buffer values outside [0, 1] undefined in a depth aspect, but allows them for `Depth16Unorm`. No M1 consumer uploads depth and one rule for both formats is simpler; allowing `Depth16Unorm` uploads later is additive. `WriteTexture` takes color formats only | every texture location |
| `BytesPerRow` is at least `Width * texelSize`; for buffer-texture copies it is a multiple of 256 and `BufferOffset` a multiple of the texel size and of 4. The buffer holds `BufferOffset + BytesPerRow * (Height - 1) + Width * texelSize` bytes | `TextureBufferCopy` |
| `data` holds `BytesPerRow * (Height - 1) + Width * texelSize` bytes, any row pitch; the front end repacks it into the ring | `WriteTexture` |
| Same format, sample count 1, the destination region `(DestinationX, DestinationY, Width, Height)` inside its mip level, no overlap when the source and destination are the same subresource | `TextureCopy` |

The 256-byte row pitch is WebGPU's rule, adopted so a D3D12 backend (whose copies need 256-byte pitches) needs no row repacking; D3D12's 512-byte footprint offset alignment stays that backend's concern, handled in its own staging. Vulkan accepts the pitch exactly (`bufferRowLength = BytesPerRow / texelSize`). It costs nothing for `WriteTexture`, which repacks. Rows per image are always `Height`, because a copy covers one layer.

## 7. Shaders and pipelines

By decision (maintainer, 2026-11-26; [ADR-0012](../adr/0012-m1-shader-cooking.md)), M1 shaders are Slang sources cooked offline to SPIR-V by `tools/BrutalEngine.Cook`, committed, and embedded as resources in the consuming assembly. The runtime never parses shader source (docs/03 3.8).

```csharp
public sealed class GraphicsDevice
{
    public Result<ShaderModuleHandle> CreateShaderModule(in ShaderModuleDescription description);
    public Result<BindGroupLayoutHandle> CreateBindGroupLayout(in BindGroupLayoutDescription description);
    public Result<PipelineLayoutHandle> CreatePipelineLayout(in PipelineLayoutDescription description);
    public Result<GraphicsPipelineHandle> CreateGraphicsPipeline(GraphicsPipelineDescription description);
    public Result<ComputePipelineHandle> CreateComputePipeline(ComputePipelineDescription description);
}

public readonly record struct ShaderModuleDescription
{
    public required ReadOnlyMemory<ShaderCode> Code { get; init; }  // one or more formats; the backend picks its own
    public string? DebugName { get; init; }
}

public readonly record struct ShaderCode(ShaderCodeFormat Format, ReadOnlyMemory<byte> Bytes);
public enum ShaderCodeFormat { SpirV }                               // later backends add their formats
```

- A module can carry several code formats, so a future Metal or D3D12 backend only adds a `ShaderCodeFormat` and the cooker emits it; SPIR-V is just bytes here. A module with no format the backend consumes returns `Unsupported`.
- The front end checks SPIR-V structurally (length a multiple of 4, the magic number `0x07230203`) and throws `ArgumentException`, because embedded bytecode that fails this is a build error, not a runtime condition. A driver rejecting valid-looking code returns `ErrorKind.InvalidData`.
- **Pipeline layouts are declared in C#**: there is no reflection data in M1, so the code that owns a shader declares its bind group layouts and push-constant range next to it. M2's cooker adds reflection blobs, and a conformance check will then compare declared layouts with reflected ones. This is the one place M1 deviates from docs/03 3.8 ("cooked SPIR-V plus reflection data"), by the shader-bytecode decision above.

```csharp
public readonly record struct PipelineLayoutDescription
{
    public required ReadOnlyMemory<BindGroupLayoutHandle> BindGroupLayouts { get; init; } // index = group (set) number
    public PushConstantRange PushConstants { get; init; }            // Size 0 = none; <= MaxPushConstantBytes
    public string? DebugName { get; init; }
}

public readonly record struct PushConstantRange(ShaderStages Stages, int Size);  // Size a multiple of 4
[Flags] public enum ShaderStages { None = 0, Vertex = 1, Fragment = 2, Compute = 4 }

public sealed record GraphicsPipelineDescription
{
    public required PipelineLayoutHandle Layout { get; init; }
    public required ShaderStage Vertex { get; init; }
    public ShaderStage? Fragment { get; init; }
    public ReadOnlyMemory<VertexBufferLayout> VertexBuffers { get; init; }
    public PrimitiveTopology Topology { get; init; }                 // TriangleList by default
    public RasterState Raster { get; init; }
    public DepthState? Depth { get; init; }                          // no stencil in M1
    public required ReadOnlyMemory<ColorTargetState> ColorTargets { get; init; }
    public int SampleCount { get; init; } = 1;
    public string? DebugName { get; init; }
}

public sealed record ComputePipelineDescription
{
    public required PipelineLayoutHandle Layout { get; init; }
    public required ShaderStage Compute { get; init; }
    public string? DebugName { get; init; }
}

public readonly record struct ShaderStage(ShaderModuleHandle Module, string EntryPoint);
public readonly record struct VertexBufferLayout(int Stride, VertexStepMode StepMode, ReadOnlyMemory<VertexAttribute> Attributes);
public readonly record struct VertexAttribute(int Location, VertexFormat Format, int Offset);
public readonly record struct ColorTargetState(TextureFormat Format, BlendState? Blend, ColorWriteMask WriteMask = ColorWriteMask.All);
public readonly record struct BlendState(BlendComponent Color, BlendComponent Alpha)
{
    public static BlendState AlphaBlending { get; }           // src-alpha, one-minus-src-alpha
    public static BlendState PremultipliedAlpha { get; }
    public static BlendState Additive { get; }
}
public readonly record struct BlendComponent(BlendFactor Source, BlendFactor Destination, BlendOperation Operation = BlendOperation.Add);
public enum BlendFactor { Zero, One, SourceColor, OneMinusSourceColor, SourceAlpha, OneMinusSourceAlpha, DestinationColor, OneMinusDestinationColor, DestinationAlpha, OneMinusDestinationAlpha }
public enum BlendOperation { Add, Subtract, ReverseSubtract, Min, Max }
[Flags] public enum ColorWriteMask { None = 0, Red = 1, Green = 2, Blue = 4, Alpha = 8, All = 15 }
public readonly record struct RasterState(CullMode CullMode = CullMode.None, FrontFace FrontFace = FrontFace.CounterClockwise);
public enum CullMode { None, Front, Back }
public enum FrontFace { CounterClockwise, Clockwise }
public readonly record struct DepthState(TextureFormat Format, bool Write, CompareFunction Compare = CompareFunction.LessOrEqual);
public enum CompareFunction { Never, Less, Equal, LessOrEqual, Greater, NotEqual, GreaterOrEqual, Always }
public enum PrimitiveTopology { TriangleList, TriangleStrip, LineList, LineStrip }
public enum VertexStepMode { Vertex, Instance }
public enum VertexFormat { Float32, Float32x2, Float32x3, Float32x4, Float16x2, Float16x4, Unorm8x4, Uint8x4, Uint32, Uint32x2, Sint32 }
```

- **Render target formats are part of the pipeline** (`ColorTargets`, `Depth.Format`, `SampleCount`), because dynamic rendering has no render pass objects: the backend passes them as `VkPipelineRenderingCreateInfo` (#55). A pass whose attachments don't match the bound pipeline's formats throws `InvalidOperationException` at `SetPipeline`.
- Viewport and scissor are always dynamic state (section 9). Everything else is baked, so a pipeline never needs recompiling at draw time.
- Pipeline descriptions are `sealed record` classes: they hold arrays and are created at load time, never per frame. Creating a pipeline may take milliseconds (driver compilation); pipeline caching on disk is out of scope for M1.

## 8. Bind groups

```csharp
public sealed class GraphicsDevice
{
    public Result<BindGroupHandle> CreateBindGroup(in BindGroupDescription description);
}

public readonly record struct BindGroupLayoutDescription
{
    public required ReadOnlyMemory<BindGroupLayoutEntry> Entries { get; init; }
    public string? DebugName { get; init; }
}

public readonly record struct BindGroupLayoutEntry(int Binding, ShaderStages Visibility, BindingType Type, int Count = 1);
public enum BindingType { UniformBuffer, StorageBuffer, ReadOnlyStorageBuffer, SampledTexture, StorageTexture, Sampler }

public readonly ref struct BindGroupDescription
{
    public required BindGroupLayoutHandle Layout { get; init; }
    public required ReadOnlySpan<BindGroupEntry> Entries { get; init; }
    public BindGroupLifetime Lifetime { get; init; }               // Persistent (default) or Frame
    public string? DebugName { get; init; }                        // ignored for Frame groups
}

public readonly record struct BindGroupEntry          // created through the factories, one per binding (and array element)
{
    public static BindGroupEntry FromBuffer(int binding, in BufferBinding buffer);
    public static BindGroupEntry FromTransient(int binding, in TransientAllocation allocation, long size);
    public static BindGroupEntry FromTexture(int binding, TextureViewHandle view, int arrayElement = 0);
    public static BindGroupEntry FromSampler(int binding, SamplerHandle sampler, int arrayElement = 0);

    public BindGroupEntryKind Kind { get; }            // the read side, for the front end's validation and backends
    public int Binding { get; }
    public int ArrayElement { get; }
    public BufferBinding Buffer { get; }               // Kind Buffer; a transient entry reads as its ring alias and offset
    public TextureViewHandle View { get; }             // Kind Texture
    public SamplerHandle Sampler { get; }              // Kind Sampler
}

public enum BindGroupEntryKind { Buffer, Texture, Sampler }

public readonly record struct BufferBinding(BufferHandle Buffer, long Offset = 0, long Size = 0);   // Size 0 = to the end
public enum BindGroupLifetime { Persistent, Frame }
```

- **Immutable.** A bind group never changes after creation. To bind different resources, create another group. That keeps the frame planner's resource-usage tracking exact (section 10) and maps to every backend.
- **Persistent groups** live until destroyed: material textures, samplers, long-lived buffers. Creating one allocates (a copy of its entries for usage tracking) and belongs to load time.
- **Frame groups** are valid only in the frame they were created in and are released with it. They are created in the frame loop with no managed allocation (the description is a `ref struct` over a span), and are the way to bind transient allocations. The backend serves them from per-frame pools reset with the frame slot (#55). Creating one outside an open frame throws `InvalidOperationException`; using one in a later frame throws `StaleHandleException`.
- **Validation** against the layout: every binding filled exactly once with a resource of the declared type and the needed usage (`Uniform`, `Storage`, `Sampled`, ...), uniform offsets aligned to `MinUniformBufferOffsetAlignment`. Violations throw `ArgumentException`. Layouts reject duplicate binding numbers and `Count < 1`.

## 9. Command lists and passes

A `CommandList` (docs/03 3.8's object) is the recorder of the open frame. It encodes commands into the frame slot's command stream, which is unmanaged memory from `GraphicsDeviceOptions.Allocator`, grown only on overflow and kept across frames. **Recording is single-threaded in M1:** one `CommandList` per frame, used on the device's thread. Parallel recording (several lists per frame, recorded in jobs) is a later, additive change.

```csharp
public readonly struct CommandList
{
    public RenderPassEncoder BeginRenderPass(in RenderPassDescription description);
    public ComputePassEncoder BeginComputePass(DebugLabel label = default);
    public Result<TextureHandle> CreateTransientTexture(in TextureDescription description); // section 10
    public void PushDebugGroup(DebugLabel label);
    public void PopDebugGroup();
    // AcquireSwapchainImage (section 3), AllocateTransient and copies (section 6)
}

public readonly ref struct RenderPassDescription
{
    public required ReadOnlySpan<ColorAttachment> ColorAttachments { get; init; }  // <= MaxColorTargets
    public DepthAttachment? Depth { get; init; }
    public DebugLabel Label { get; init; }                                         // also names the timestamp
}

public readonly record struct ColorAttachment(TextureViewHandle View, LoadOp Load, StoreOp Store, ColorValue Clear = default);
public readonly record struct DepthAttachment(TextureViewHandle View, LoadOp Load, StoreOp Store, float Clear = 1);
public enum LoadOp { Load, Clear, DontCare }
public enum StoreOp { Store, DontCare }
public readonly record struct ColorValue(float R, float G, float B, float A);   // linear; sRGB targets encode it

public readonly struct RenderPassEncoder
{
    public void SetPipeline(GraphicsPipelineHandle pipeline);
    public void SetBindGroup(int index, BindGroupHandle group);
    public void SetVertexBuffer(int slot, BufferHandle buffer, long offset = 0);
    public void SetIndexBuffer(BufferHandle buffer, IndexFormat format, long offset = 0);
    public void SetViewport(in Viewport viewport);             // defaults to the full attachment at BeginRenderPass
    public void SetScissor(in ScissorRect scissor);            // defaults to the full attachment
    public void PushConstants<T>(in T data, int offset = 0) where T : unmanaged;
    public void Draw(in DrawArguments arguments);
    public void DrawIndexed(in DrawIndexedArguments arguments);
    public void PushDebugGroup(DebugLabel label);
    public void PopDebugGroup();
    public void End();
}

public readonly struct ComputePassEncoder
{
    public void SetPipeline(ComputePipelineHandle pipeline);
    public void SetBindGroup(int index, BindGroupHandle group);
    public void PushConstants<T>(in T data, int offset = 0) where T : unmanaged;
    public void Dispatch(int groupsX, int groupsY = 1, int groupsZ = 1);
    public void End();
}

public readonly record struct Viewport(float X, float Y, float Width, float Height, float MinDepth = 0, float MaxDepth = 1); // pixels
public readonly record struct ScissorRect(int X, int Y, int Width, int Height);   // pixels, inside the attachments
public enum IndexFormat { Uint16, Uint32 }
public readonly record struct DrawArguments(int VertexCount, int InstanceCount = 1, int FirstVertex = 0, int FirstInstance = 0);
public readonly record struct DrawIndexedArguments(int IndexCount, int InstanceCount = 1, int FirstIndex = 0, int BaseVertex = 0, int FirstInstance = 0);
```

- **Passes map to dynamic rendering.** A render pass is described per frame by its attachments and load/store operations, and the Vulkan backend records it as `vkCmdBeginRendering`/`vkCmdEndRendering` (docs/03 3.8; BRUTAL binds it, `Brutal.Vulkan/Generated/Handles/VkDevice.gen.cs:208`; M0 uses it, `FrameRenderer.cs:309-316`). There are no render pass or framebuffer objects in the API.
- Draw arguments are structs so that the 4- and 5-value draws follow the options rule of docs/07, and because their layout matches the GPU's indirect-draw records, which indirect draws will reuse.
- **Encoding rules** (each violation throws `InvalidOperationException`, checked when the command is encoded): one pass open at a time, and `End` before the next pass, a copy or `SubmitFrame`; draws need a pipeline, every bind group its layout declares, and the vertex buffers it declares; bind groups must match the pipeline layout's group at that index; push constants must lie inside the layout's range; a texture can't be both an attachment and sampled in the same pass; debug groups must balance within a pass and within the frame.
- `PushConstants<T>` copies `T`'s bytes as they are in memory. Matrix layout is the shader's business: S4 found Slang decorates push-constant matrices RowMajor even when ColumnMajor is requested (`docs/spikes/README.md:88`), so the conformance suite pins it with a GPU round trip (section 15).
- Every encoder method is allocation-free and only appends to the command stream; nothing reaches the GPU until `SubmitFrame`.

## 10. The minimal frame graph

docs/03 3.8 asks for a small frame graph that orders passes, inserts `synchronization2` barriers and handles transient render targets, with no explicit barriers in the public API. In M1 the graph is **implicit in the recorded frame**: there is no separate declaration step. At `SubmitFrame`, the front end's planner reads the command stream and derives every pass's resource usage from what it recorded:

| Recorded | Usage derived |
|----------|---------------|
| Color / depth attachment with `Load` or `Clear` | color-target write (and read for `Load`) / depth read-write |
| Bind group entry, by binding type | uniform read, storage read or write, sampled read, at the stages in `Visibility` |
| Vertex, index buffer; indirect args | vertex input read, index read, indirect read |
| Copy source / destination; ring uploads | transfer read / transfer write |
| Acquired swapchain image | present after its last use in the frame |

- **Ordering is declaration order.** Passes run in the order they were recorded, which is a valid topological order of their dependencies by construction (a pass can only read what was written before it, in this frame or an earlier one). The planner doesn't reorder or cull passes in M1.
- **Barrier insertion is hidden.** The planner tracks each resource's last access (and, for textures, its layout) across passes and across frames. Before each pass, it emits backend-neutral `ResourceTransition`s in `Barrier` records (public, section "The backend interface"), and the backend turns them into `vkCmdPipelineBarrier2` (BRUTAL `VkDevice.gen.cs:193`). The M0 sample shows the two transitions every presented frame needs: undefined to color attachment at the acquire semaphore's stage, and color attachment to present (`FrameRenderer.cs:277-294`, `:318-333`). Ring uploads become one copy batch at the frame's start, ordered after all earlier work (section 6). A compute pass is one sync scope: to read what a dispatch wrote, start another pass.
- **Transient render targets.** `CommandList.CreateTransientTexture` returns a texture valid for this frame only (for lighting and post-processing targets, docs/03 3.8). The device keeps a pool keyed by description and reuses targets across frames. Creating one can fail like `CreateTexture` (`Unsupported`, `OutOfMemory`) the first time a description is seen; a warm pool can't fail. A transient texture's content is undefined at its first use in a frame, so that use must be `Clear` or `DontCare`; a `Load` throws `InvalidOperationException`.
- **Out of scope for M1:** reordering, pass culling, memory aliasing between transient targets, async compute and multiple queues (the transfer-queue path in docs/03 3.8 is the backend's concern, #55), split barriers, explicit resource declarations, and graph persistence across frames. All can be added behind the same recording API.
- The planner is pure (command stream in, transitions out) and unit-tested without a GPU; its GPU behaviour is proven by the conformance suite's barrier cases under synchronization validation.

## 11. Timestamp queries

GPU time per pass is shown on the debug overlay (docs/03 3.8 "GPU timestamps for each pass").

```csharp
public sealed class GraphicsDevice
{
    public DebugLabel CreateDebugLabel(string name);              // interned once; allocates the first time only
    public string GetDebugLabelName(DebugLabel label);            // the interned string, no allocation
    public bool TryGetPassTimings(long frameNumber, Span<PassTiming> destination, out int written);
}

public readonly record struct PassTiming(int PassIndex, DebugLabel Label, double Milliseconds);
```

- With `GraphicsFeatures.TimestampQueries` enabled, the backend writes a timestamp at the start and end of every labelled pass into the frame slot's query pool (BRUTAL binds `vkCmdResetQueryPool`, `vkCmdWriteTimestamp2` and `vkGetQueryPoolResults`, `VkDevice.gen.cs:92`, `:194`, `:67`).
- **No stalls:** results for frame N are read without waiting once `CompletedFrameNumber >= N`, which with 2 frames in flight is about two frames later. `TryGetPassTimings` returns `false` until then, and for frames older than `FramesInFlight` slots (their queries were reused). It never waits on the GPU.
- **Capability gating:** the feature is available when the graphics queue's `timestampValidBits` is non-zero (`VkQueueFamilyProperties.gen.cs:39`), converted with `TimestampPeriodNanoseconds`. Without it, `TryGetPassTimings` always returns `false` and the overlay shows "GPU timing unavailable". On the RTX 2070 SUPER, `vulkaninfo` (Windows 11, 2026-11-26) reported `timestampComputeAndGraphics = true` (`VkPhysicalDeviceLimits.gen.cs:675`), `timestampPeriod = 1` and `timestampValidBits = 64`; lavapipe and MoltenVK are unverified (G11).
- Labels are interned `DebugLabel` handles, so the frame loop never builds strings. `default(DebugLabel)` means unlabelled: no timestamp and no debug group name. So `CreateDebugLabel` rejects an empty name with `ArgumentException`, as it does `null` with `ArgumentNullException`.

## 12. Device lost

Decision (maintainer, 2026-11-26): detection everywhere, a typed event, and recreation through owner-registered callbacks. The Vulkan mechanics (one result wrapper that sees every `VK_ERROR_DEVICE_LOST`, `Brutal.Vulkan/Generated/Enums/VkResult.gen.cs:185`) are in #55.

```csharp
public sealed class GraphicsDevice
{
    public bool IsLost { get; }
    public event EventHandler<DeviceLostEventArgs>? DeviceLost;           // raised once per loss
    public GpuOwnerRegistration RegisterOwner(IGpuResourceOwner owner);  // a disposable token: dispose to unregister
    public Result Recover();
}

public sealed class DeviceLostEventArgs : EventArgs
{
    public string Operation { get; }    // where it was detected: "BeginFrame", "SubmitFrame", "AcquireSwapchainImage", ...
    public int NativeCode { get; }      // the backend's status code
    public bool TimedOut { get; }       // GpuTimeout expired (a hung GPU), rather than a reported loss
}

public sealed class GpuOwnerRegistration : IDisposable   // returned by RegisterOwner; owned by the caller
{
    public void Dispose();                                 // unregisters; idempotent; owner thread only
}

public interface IGpuResourceOwner
{
    void OnDeviceLost(GraphicsDevice device);        // drop every handle; don't call the device
    Result OnDeviceRecreated(GraphicsDevice device); // recreate and re-upload
}
```

- **Detection:** any backend call that reports loss (or a `GpuTimeout` expiry) marks the device lost. `BeginFrame`, `SubmitFrame` and `AcquireSwapchainImage` return `DeviceLost`; `Create*` and `Write*` return `ErrorKind.Unavailable`; encoder calls in a lost frame become no-ops, so recording code needs no checks. The event is raised once, synchronously, from the call that detected the loss.
- **Recovery is the owner's call:** the host (Runtime) calls `Recover()` outside a frame. It calls every owner's `OnDeviceLost` in reverse registration order, destroys all native objects, invalidates **every** resource handle (each handle allocator is cleared, so all old handles are stale), creates a new backend device on the same adapter selection, recreates every swapchain for its window, then calls `OnDeviceRecreated` in registration order. If the adapter is gone, or an owner's recreation fails, `Recover` returns the failure and the host treats it as fatal (docs/03 3.2: graphics failures are fatal).
- **What survives:** the `GraphicsDevice` object, its options, logger and statistics, registered owners, `SwapchainHandle`s (recreated in place, so their next acquire reports `Recreated`) and `DebugLabel`s. Everything else is gone: buffers, textures, views, samplers, shader modules, layouts, bind groups, pipelines and their contents.
- **Owners in M1** are the engine's own: the overlay's font atlas and pipelines, the sprite renderer, the splash. M2's asset database registers as one owner and reloads what it loaded, which is how docs/03 3.5's "recreates GPU resources from the asset database" is reached through the same contract.
- **Fault injection** is internal: the front end wraps every backend status in one place, so a test can make the N-th call of a chosen operation report loss. `Recover()` then disposes that backend device and creates a new one, so teardown and recreation are real on every backend. It isn't public API.
- **Who may use the seam.** Decision (maintainer, 2026-11-26): `BrutalEngine.Graphics` grants `InternalsVisibleTo` to `BrutalEngine.Graphics.Tests` and also to `BrutalEngine.Graphics.Conformance`. This is one named exception to docs/07's "matching test project only" rule. The conformance suite has to force device loss on every backend, and the seam belongs in the backend-neutral front end. The attribute carries a comment giving that reason, and no other assembly gets the exception.

## 13. Errors and diagnostics

- **Typed errors:** expected failures are Core `EngineError`s ([errors](errors.md)) with the `ErrorKind`s of section 5's table. The message names the operation and the object's debug name ("CreateTexture 'SpriteAtlas': format Rgba16Float can't be a storage target on this device"), and `NativeCode` carries the backend status. Misuse exceptions name the call and the handle's debug name.
- **Debug names and labels:** every description's `DebugName` names the native object (`vkSetDebugUtilsObjectNameEXT`, `VkDevice.gen.cs:418`) and appears in validation messages, errors and the leak report. Debug groups and labelled passes become command labels (`vkCmdBeginDebugUtilsLabelEXT`, `VkDevice.gen.cs:423`) for RenderDoc and validation output. Both cost nothing when `VK_EXT_debug_utils` is absent.
- **Validation messages** go to the `Graphics.Validation` log category of the device's logger: errors at `Error`, warnings at `Warning`, with the object names the layer reports. The device also counts them (`GraphicsStatistics.ValidationErrors`, `ValidationWarnings`), instance-owned, with no static state (the M0 sample's static counters don't carry over). Validation is on whenever `GraphicsDeviceOptions.Validation` is true; the device then fails `Create` if the layer is missing rather than running unvalidated.
- **Resource tracking and the leak report:** the device tracks every live object (docs/03 3.3 "registered with the device's resource tracker"). `Dispose` logs one `Error` line per leaked object with its kind and debug name. Tests call `ThrowIfLeaks()` before disposing, which throws `GpuResourceLeakException` listing them, just as `TrackingAllocator.ThrowIfLeaks` does for unmanaged memory ([memory](memory.md)). Both the log and the exception list objects oldest first (in creation order), one `Kind 'DebugName'` line each: an object without a debug name reads `Kind ''`, a texture's default view carries its texture's name, and a live swapchain reads `Swapchain 'DebugName'` (its images never appear). Besides the standard exception constructors, `GpuResourceLeakException` has a public `GpuResourceLeakException(IReadOnlyList<string> leakedObjects)` constructor, which keeps a read-only copy of the lines.

```csharp
public sealed class GraphicsDevice
{
    public GraphicsStatistics Statistics { get; }   // a readonly struct snapshot: no allocation
    public void ThrowIfLeaks();                      // throws GpuResourceLeakException
}

public sealed class GpuResourceLeakException : Exception
{
    public IReadOnlyList<string> LeakedObjects { get; }    // one "Kind 'DebugName'" line per live object
}

public readonly record struct GraphicsStatistics(
    long FramesSubmitted, long UploadBytesLastFrame, long UploadOverflowBytes,
    int LiveObjects, long ValidationErrors, long ValidationWarnings);
```

## 14. Threading and allocation

- **One owner thread.** The thread that calls `GraphicsDevice.Create` owns the device: the main thread in the Runtime host (docs/03 3.2). Every `GraphicsDevice` member except `Capabilities` and `Adapter`, and every `CommandList` and encoder member, checks it and throws `WrongThreadException` with the API name (docs/03 3.2). The device captures Core's `ThreadAffinity.CurrentThread()` at creation and calls `ThrowIfNotCurrentThread(apiName)`; both `ThreadAffinity` and `WrongThreadException` are Core types added by P1 ([platform note section 9](platform.md#9-threading)). The check is always on, not just in debug builds, as in Platform: it is one integer compare.
- **Any thread:** descriptions (immutable values), `Capabilities` and `Adapter` (immutable after `Create`). Everything else, including `GetDebugLabelName` and `Statistics`, is owner-thread only. Handles are plain values and can be passed between threads, but only used on the owner thread.
- **Job workers** (render prepare, docs/03 3.2) don't call the device in M1. They write into memory the main thread got from `AllocateTransient` before scheduling them, and the main thread submits after the jobs complete.
- **Zero managed allocation in steady-state frames.** These calls allocate nothing (a test measures each across 1,000 frames): `BeginFrame`, `SubmitFrame`, `AcquireSwapchainImage`, every encoder method, `AllocateTransient`, frame bind groups, `WriteBuffer`, `WriteTexture`, `TryReadBuffer`, copies, `CreateTransientTexture` once the pool is warm, `Destroy`, `TryGetPassTimings`, `Statistics`. Creating persistent objects (buffers, textures, pipelines, persistent bind groups, labels the first time) may allocate and belongs to load time; doing it in a frame is allowed but makes that frame non-steady. Swapchain recreation and device recovery allocate.
- **Unmanaged memory** (command streams, deletion queues, usage tracking, ring bookkeeping) comes from `GraphicsDeviceOptions.Allocator`, an `INativeAllocator`; tests pass a `TrackingAllocator` and call `ThrowIfLeaks` ([memory](memory.md)). GPU memory goes through VMA in the Vulkan backend (docs/03 3.3), reported by `ThrowIfLeaks` above.

## 15. The conformance contract

Every backend must pass `tests/BrutalEngine.Graphics.Conformance` (ADR-0002; docs/04's layout), on every OS CI covers with software Vulkan, and locally on the RTX 2070 SUPER per graphics PR (decision, maintainer, 2026-11-26: real-GPU runs are local until a self-hosted runner exists). The suite's rules:

- **Validation-clean:** every test runs with `Validation = true` and fails on any validation error or warning, except an explicit allow-list where each entry has a comment saying why ([Testing strategy](../05-testing-strategy.md#gpu-testing-without-gpus)). Synchronization validation is enabled.
- **Negative canary:** one test calls the backend's `InjectValidationError()` through the front end's internal seam (as the M0 harness's `--inject-validation-error` does) and must see `ValidationErrors` grow, which proves the layer is loaded.
- **Leak-free:** every test ends with `device.ThrowIfLeaks()` and its `TrackingAllocator.ThrowIfLeaks()`.
- **Readback with tolerance:** rendered results are copied to `Readback` buffers and compared with expected pixels or golden images by a per-channel threshold plus a maximum differing-pixel ratio (docs/05 "Golden image"). References are produced on lavapipe; real GPUs may use a slightly wider tolerance, and any test that needs more than the default says why.
- **No skipping on capabilities:** a test whose feature is missing asserts the documented unsupported behaviour (`Result` is `Unsupported`, `TryGetPassTimings` is `false`) instead of skipping.

Test categories (C1 starts them; each later graphics issue adds its cases):

| Category | Covers |
|----------|--------|
| Device | create/dispose, adapter selection and rejection reasons, overrides, capabilities, required/optional features, `Create` failures |
| Handles and lifetime | stale, null and foreign handles per kind; destroy-in-frame; deferred destruction after `FramesInFlight` frames; dependent views and bind groups |
| Resources and formats | every format: create, clear or upload, read back, round trip; unsupported combinations return `Unsupported`; description validation |
| Uploads and readback | buffer and texture writes, write-then-use ordering across frames, ring overflow, transient allocations, `TryReadBuffer` never blocking |
| Passes and pipelines | clears with each load/store op, a triangle golden image, vertex formats, blending presets, depth, viewport and scissor, push constants (with the S4 RowMajor round trip), compute writes |
| Frame graph | write-then-sample, copy-then-sample, storage write then read, transient targets reused across frames, swapchain images presented, under synchronization validation |
| Bind groups | persistent and frame lifetimes, layout mismatch, stale-resource binding, offset alignment |
| Swapchain | headless (Linux lavapipe) and windowed (Windows lavapipe); macOS (MoltenVK) is best-effort and unverified, not run in CI ([ADR-0015](../adr/0015-tested-platforms-windows-linux.md)); resize, minimise, out-of-date, suboptimal, surface lost (the last three forced through the fault seam: this is docs/05's "mock surface layer") |
| Timestamps | results arrive without waiting, or the unsupported path |
| Device lost | loss forced at fence wait, submit, acquire, present and resource creation; event raised once; `Recover` recreates swapchains and calls owners; every old handle stale |
| Misuse | every exception documented in this note |
| Allocation | 1,000 steady-state frames of a representative scene allocate 0 managed bytes |

## The backend interface

A backend implements these public types in `BrutalEngine.Graphics.Backend`. They are part of this note's contract, because the Vulkan backend note (#55) implements them and doesn't change this note. Only the front end calls them; games never do. Nothing here names a Platform type: the window reaches a backend only as a `WindowSurfaceSource` (section 3).

```csharp
namespace BrutalEngine.Graphics.Backend;

public interface IGraphicsBackend                       // stateless; created and owned by the host
{
    string Name { get; }                                // "Vulkan"
    Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(BackendDeviceRequest request);
    Result<IBackendDevice> CreateDevice(BackendDeviceRequest request, int adapterIndex, BackendObjectTable objects);
}

public sealed record BackendDeviceRequest              // built by the front end from GraphicsDeviceOptions
{
    public required Logger Logger { get; init; }
    public required INativeAllocator Allocator { get; init; }
    public WindowSurfaceSource? CompatibleSurface { get; init; }   // the only window link; never an IWindow
    public bool Validation { get; init; }
    public int FramesInFlight { get; init; }
    public GraphicsFeatures RequiredFeatures { get; init; }
    public GraphicsFeatures OptionalFeatures { get; init; }
    public TimeSpan GpuTimeout { get; init; }
    public string ApplicationName { get; init; } = "";
}

public interface IBackendDevice : IDisposable           // owned and disposed by GraphicsDevice
{
    GraphicsAdapterInfo Adapter { get; }
    GraphicsCapabilities Capabilities { get; }
    FormatSupport GetFormatSupport(TextureFormat format);

    // Objects. Ids are non-zero, backend-chosen and opaque; handles inside descriptions resolve through the
    // BackendObjectTable. Destroy is called only once the object's last frame has completed.
    Result<BackendBuffer> CreateBuffer(in BufferDescription description, BackendMemory memory);
    Result<ulong> CreateTexture(in TextureDescription description);
    Result<ulong> CreateTextureView(in TextureViewDescription description);
    Result<ulong> CreateSampler(in SamplerDescription description);
    Result<ulong> CreateShaderModule(in ShaderModuleDescription description);
    Result<ulong> CreateBindGroupLayout(in BindGroupLayoutDescription description);
    Result<ulong> CreatePipelineLayout(in PipelineLayoutDescription description);
    Result<ulong> CreateBindGroup(in BindGroupDescription description);  // Frame lifetime: from the open slot's pool
    Result<ulong> CreateGraphicsPipeline(GraphicsPipelineDescription description);
    Result<ulong> CreateComputePipeline(ComputePipelineDescription description);
    void SetDebugName(BackendObjectKind kind, ulong id, ReadOnlySpan<byte> utf8Name);
    void Destroy(BackendObjectKind kind, ulong id);

    // Surfaces and swapchains (rules below the code).
    Result<ulong> CreateSurface(WindowSurfaceSource source);
    void DestroySurface(ulong surface);
    Result<BackendSwapchain> CreateSwapchain(in BackendSwapchainRequest request);
    void DestroySwapchain(ulong swapchain);             // the swapchain and its image views, never the surface
    void GetSwapchainImage(ulong swapchain, int imageIndex, out ulong texture, out ulong view); // after each create
    BackendAcquire AcquireImage(ulong swapchain, int frameSlot);
    BackendPresent Present(ulong swapchain, int imageIndex);

    // Frames.
    Result WaitForFrame(int frameSlot);                 // blocks up to GpuTimeout; then the slot's resources are free
    Result<bool> IsFrameComplete(int frameSlot);        // never blocks
    Result BeginFrame(int frameSlot);                   // resets the slot's command, descriptor and query pools
    Result Execute(in FramePlan plan);                  // records and submits; presents come after, through Present
    bool TryReadTimestamps(int frameSlot, Span<ulong> ticks); // false until the slot's frame completed
    Result WaitIdle();

    // Diagnostics.
    long ValidationErrors { get; }
    long ValidationWarnings { get; }
    void InjectValidationError();                       // the negative canary; throws without Validation (below)
}

public enum BackendMemory { DeviceLocal, DeviceLocalHostVisiblePreferred, HostUpload, HostReadback }
public readonly record struct BackendBuffer(ulong Id, nint MappedPointer);   // MappedPointer: HostUpload, HostReadback
public enum BackendObjectKind { Buffer, Texture, TextureView, Sampler, ShaderModule, BindGroupLayout, PipelineLayout, BindGroup, GraphicsPipeline, ComputePipeline }
public readonly record struct BackendSwapchainRequest(ulong Surface, WindowSurfaceSource Source, SwapchainOptions Options, ulong OldSwapchain);
public readonly record struct BackendSwapchain(ulong Id, SwapchainInfo Info);
public readonly record struct BackendAcquire(SwapchainStatus Status, int ImageIndex, int NativeCode);
public readonly record struct BackendPresent(SwapchainStatus Status, int NativeCode);
public enum SwapchainStatus { Success, Suboptimal, OutOfDate, SurfaceLost, DeviceLost }

public sealed class BackendObjectTable                  // owned by the front end; read-only for the backend
{
    public ulong GetId(BufferHandle handle);            // one overload per handle kind; validated handles only
    public ulong GetId(TextureViewHandle handle);       // ... TextureHandle, SamplerHandle, ShaderModuleHandle, layouts, pipelines
    public ReadOnlySpan<byte> GetLabel(DebugLabel label); // UTF-8 name of an interned label
}
```

**Device creation.** `CreateDevice` takes the `Index` of an adapter from an `EnumerateAdapters` call with the same request, and an adapter that disappeared since that enumeration fails with `ErrorKind.Unavailable`.

**Acquire.** `BackendAcquire.ImageIndex` is the acquired image's index when the status is `Success` or `Suboptimal`, and -1 for every other status.

**Surfaces and swapchains:**

- **Surfaces.** `CreateSurface` makes the window's one live surface (platform note section 4). The front end passes the same `WindowSurfaceSource` instance for the `CompatibleWindow` as `BackendDeviceRequest.CompatibleSurface`, so the first `CreateSurface` for that instance returns the selection surface, the one the backend created for adapter selection, instead of a second one. `IBackendDevice.Dispose` destroys any surface still alive, including an unused selection surface.
- **Recreation.** `CreateSwapchain` with a non-zero `OldSwapchain` reuses that swapchain's surface (`Surface` is the same id) and retires the old swapchain. The front end then calls `DestroySwapchain` on the old id whether the create succeeded or failed, as M0 does (`HelloWindowApp.cs:165-168`), after waiting for the device to go idle.
- **Destruction.** `GraphicsDevice.Destroy(SwapchainHandle)` lands on `DestroySwapchain`, then `DestroySurface`, once the swapchain's frames have completed (through the deletion queue). Swapchain image texture and view ids from `GetSwapchainImage` are owned by their swapchain: they never go to `Destroy`, and are invalid after `DestroySwapchain`.

**The frame plan.** `SubmitFrame` hands the backend one `FramePlan`: the slot's command stream after planning, with every handle already resolved to a backend id and every barrier inserted (section 10).

```csharp
public readonly ref struct FramePlan
{
    public int FrameSlot { get; }
    public long FrameNumber { get; }
    public ReadOnlySpan<byte> Commands { get; }          // read with CommandReader; valid during Execute only
    public ReadOnlySpan<SwapchainImageUse> Acquired { get; } // images Execute must wait for and leave presentable
}

public readonly record struct SwapchainImageUse(ulong Swapchain, int ImageIndex);

public ref struct CommandReader
{
    public CommandReader(ReadOnlySpan<byte> commands);
    public bool MoveNext();                              // false at the end of the stream
    public readonly CommandKind Kind { get; }
    public readonly ref readonly T Payload<T>() where T : unmanaged;      // throws if T isn't Kind's payload type
    public readonly ReadOnlySpan<T> Trailing<T>() where T : unmanaged;    // the variable-length part, if any
}

public readonly struct ResourceTransition               // barrier element; unmanaged
{
    public ulong Resource { get; init; }                 // buffer or texture id
    public bool IsTexture { get; init; }
    public ResourceState Before { get; init; }
    public ResourceState After { get; init; }
    public ShaderStages BeforeStages { get; init; }      // for shader states
    public ShaderStages AfterStages { get; init; }
    public int BaseMipLevel { get; init; }
    public int MipLevelCount { get; init; }
    public int BaseArrayLayer { get; init; }
    public int ArrayLayerCount { get; init; }
}

public enum CommandKind : ushort
{
    Barrier, CopyBufferToBuffer, CopyBufferToTexture, CopyTextureToBuffer, CopyTextureToTexture,
    BeginRenderPass, EndRenderPass, BeginComputePass, EndComputePass, SetPipeline, SetBindGroup,
    SetVertexBuffer, SetIndexBuffer, SetViewport, SetScissor, PushConstants, Draw, DrawIndexed, Dispatch,
    PushDebugGroup, PopDebugGroup, ClearSwapchainImage,
}

public enum ResourceState { Undefined, CopySource, CopyDestination, VertexBuffer, IndexBuffer, IndirectBuffer, UniformRead, StorageRead, StorageReadWrite, SampledRead, ColorTarget, DepthRead, DepthReadWrite, Present }
```

A `ResourceTransition` for a buffer (`IsTexture` false) covers the whole buffer, and its mip and layer fields are 0; for a texture they name the range it covers, already inside the texture.

Each record in the stream is a `CommandKind` with an unmanaged payload struct of the same name plus `Command` (all public, in this namespace):

| `CommandKind` | Payload fields | Trailing |
|---------------|----------------|----------|
| `Barrier` | `BarrierCommand`: `Count` | `ResourceTransition[Count]` |
| `CopyBufferToBuffer` | `CopyBufferToBufferCommand`: source id, source offset, destination id, destination offset, size | none |
| `CopyBufferToTexture`, `CopyTextureToBuffer` | `CopyBufferTextureCommand`, shared (the kind gives the direction): buffer id, offset, bytes per row, texture id, mip, layer, aspect, region | none |
| `CopyTextureToTexture` | `CopyTextureToTextureCommand`: per side, as `TextureCopy`'s two `TextureLocation`s, a texture id, mip, layer and aspect (source first); then the source region, destination x, y | none |
| `BeginRenderPass` | `BeginRenderPassCommand`: color count, has depth, label, timestamp query index (-1 = none) | `AttachmentCommand[color count + (has depth ? 1 : 0)]`, colors first: view id, load, store, color clear (`ColorValue`, used by color attachments), depth clear (`float`, used by the depth attachment) |
| `BeginComputePass` | `BeginComputePassCommand`: label, timestamp query index (-1 = none) | none |
| `EndRenderPass`, `EndComputePass` | `EndPassCommand`, shared: timestamp query index (-1 = none) | none |
| `SetPipeline` | `SetPipelineCommand`: pipeline id, is compute | none |
| `SetBindGroup` | `SetBindGroupCommand`: index, bind group id | none |
| `SetVertexBuffer`, `SetIndexBuffer` | `SetVertexBufferCommand`: slot, buffer id, offset; `SetIndexBufferCommand`: `IndexFormat`, buffer id, offset (one struct each) | none |
| `SetViewport`, `SetScissor` | `SetViewportCommand` holding a `Viewport`, `SetScissorCommand` holding a `ScissorRect` | none |
| `PushConstants` | `PushConstantsCommand`: stages (`ShaderStages`), offset, size | `byte[size]` |
| `Draw`, `DrawIndexed`, `Dispatch` | `DrawCommand` holding a `DrawArguments`, `DrawIndexedCommand` holding a `DrawIndexedArguments`, `DispatchCommand`: group counts x, y, z | none |
| `PushDebugGroup`, `PopDebugGroup` | `DebugGroupCommand`, shared: label (for a pop, the innermost open group's) | none |
| `ClearSwapchainImage` | `ClearSwapchainImageCommand`: index into `FramePlan.Acquired` (an acquired image no pass wrote) | none |

Each record starts with an 8-byte header (the `CommandKind`, 2 reserved zero bytes, then the record's `Size` as a 4-byte integer), followed by the payload zero-padded to a multiple of 8 bytes, so the trailing elements after it are 8-byte aligned. The header's `Size` is 8 + the payload size rounded up to 8 + the trailing part's bytes, without the final padding, and the next record starts at `Size` rounded up to 8.

Contract rules:

| Topic | Rule |
|-------|------|
| Ownership | The host creates and keeps the `IGraphicsBackend`; it holds no native state, so it needs no disposal. Each `IBackendDevice` owns its instance, device and every object it created, and `Dispose` destroys whatever is left. The front end disposes it after `WaitIdle` and the leak report. Mapped pointers stay valid until the buffer is destroyed. |
| Threading | Every member is called on the device's owner thread, never re-entrantly. `GetRequiredInstanceExtension` on `WindowSurfaceSource` is the only member a backend may call from elsewhere. |
| Allocation | `WaitForFrame`, `IsFrameComplete`, `BeginFrame`, `AcquireImage`, `Execute`, `Present`, `TryReadTimestamps`, frame-lifetime `CreateBindGroup` and `Destroy` allocate no managed memory. |
| Errors | Every native status comes back as a `Result` or a status, never as an exception. Device loss is `ErrorKind.Unavailable` with the native code, or `SwapchainStatus.DeviceLost`. Misuse can't reach a backend: the front end validates first. |
| Swapchain creation | `CreateSwapchain` reports a zero surface extent (the window was minimised after the front end's check) as `ErrorKind.Cancelled` ("surface extent is zero") and a lost surface as `ErrorKind.NotFound`, each with the native code; `Unavailable` stays reserved for device loss. A first creation's failure reaches the caller unchanged; a recreation maps `Cancelled` to `AcquireStatus.Minimised` and `NotFound` to `AcquireStatus.SurfaceLost`. |
| Memory | `HostUpload` and `HostReadback` buffers are mapped persistently. The backend makes CPU writes visible to `Execute`'s GPU work, and GPU writes visible to the CPU once `IsFrameComplete` or `WaitForFrame` reports the frame done (coherent memory or a flush and invalidate, and for GPU writes a host-read memory dependency before the frame's completion signal). |
| Frame-call failures | `WaitForFrame` that runs out of `GpuTimeout` returns `ErrorKind.Timeout`, which the front end reports as a loss with `DeviceLostEventArgs.TimedOut`. Any other failure of `WaitForFrame`, `IsFrameComplete`, `BeginFrame` or `Execute` (for example out of memory at submit, or a failed pool reset) is also treated as device loss, carrying its native code: a frame call has no partial recovery. An `Execute` that fails before submitting leaves the slot's completion signalled, so the slot's next `WaitForFrame` returns at once instead of waiting forever. This moves M0's rule, reset the fence only once a submit is certain (`FrameRenderer.cs:13-15`), into the backend. |
| Frame slots | `BeginFrame(slot)` follows a successful `WaitForFrame(slot)`. `Execute` signals the slot's completion and makes the acquired images presentable, then the front end calls `Present` for each. Frame-lifetime bind groups are released by the slot's pool reset, never by `Destroy`. |
| Timestamps | The front end assigns two query indices per labelled pass when `TimestampQueries` is enabled. `TryReadTimestamps` returns raw ticks; the front end converts them with `TimestampPeriodNanoseconds`. |

The front end maps swapchain statuses to the public results:

| `AcquireImage` / `Present` status | Public result |
|-----------------------------------|---------------|
| `Success` | `Acquired` |
| `Suboptimal` | `Acquired` (or the frame presented), and the swapchain is recreated at the next acquire |
| `OutOfDate` at acquire | recreate (`CreateSwapchain` with the old id) and retry once: `Recreated`, or `Skipped` if still out of date |
| `OutOfDate` at present | recreated at the next acquire |
| `SurfaceLost` | `SurfaceLost`; at present, the next acquire reports it |
| `DeviceLost` | `AcquireStatus.DeviceLost` / `FrameStatus.DeviceLost`, and the device is lost (section 12); `NativeCode` fills `DeviceLostEventArgs.NativeCode` |

**Test hooks.** Neither needs an internal backend seam:

- **Device loss.** The front end's fault seam (section 12) replaces a backend status with a loss. `Recover()` then really disposes that `IBackendDevice` and creates a new one, so recreation runs for real on every backend. The front end discards, without calling `Destroy`, every id it held for the old device (deletion queues, ring and pending upload blocks, the transient pool, frame bind groups, surfaces, swapchain image ids), then recreates its rings on the new device. A backend's own loss detection is unit-tested in its own test project (#55).
- **Negative canary.** `InjectValidationError()` is public on the backend interface and documented as test-only: it makes the next `Execute` record one known violation. On a device created without `Validation` it throws `InvalidOperationException`: with no layer to catch it, the violation would be undefined behaviour on real hardware, and the test would pass without proving anything. A suitable violation is the M0 harness's wrong old layout (`FrameRenderer.cs:286-287`). The conformance suite calls it through the front end's internal seam and expects `ValidationErrors` to grow.

## 16. The issue split

The planned issues G1, G2, G10 and C1 hold, plus G2b, the device front end, which is larger than one PR on its own:

| Issue | Scope from this note | Blocked by | Est. |
|-------|----------------------|------------|------|
| G1 feat(graphics): add the render interface's resource descriptions | Creates `src/BrutalEngine.Graphics`: typed handles, formats, usages, `FormatSupport`, `SampleCounts`, buffer/texture/view/sampler descriptions and their validation rules (section 5), copy types and their rules (section 6), `AdapterSelection` and its parser, capabilities, limits and features (section 2). No Platform reference: `GraphicsDeviceOptions` moves to G2b | this note | ~650 |
| G2b feat(graphics): add the device front end and a fake backend | `GraphicsDeviceOptions`, `GraphicsDevice` and the backend interface types except the command stream records, including `FramePlan`, `SwapchainImageUse`, `ResourceTransition` and `ResourceState` (section "The backend interface"), implemented as specified there: handle tables, `BackendObjectTable`, frame state machine, deletion queues, upload ring and pending blocks, readback, debug labels, statistics, leak report, device-lost state and the fault seam (sections 1-4, 6, 11-14), `WindowSurfaceSource` (section 3); an in-memory fake backend for unit tests | G1, P1 (`IWindow`, `IVulkanSurfaceSource`, `ThreadAffinity`, the `Graphics -> Platform` edge) | ~700 |
| G2 feat(graphics): add pipelines, bind groups and command recording | Shader module, layout, pipeline and bind group descriptions with validation (sections 7-8), pass descriptions, `CommandList`/encoder recording into the command stream with its rules (section 9), `CommandKind`, the payload structs and `CommandReader` | G2b (handle tables, open-frame state) | ~650 |
| G10 feat(graphics): order passes and insert barriers with a minimal frame graph | The planner (section 10): usage derivation, cross-frame state tracking, `ResourceTransition`s, the transient texture pool; pure unit and property tests | G2 | ~650 |
| C1 test(graphics): add the conformance suite and run it in CI | Section 15's rules and harness (validation fixture, canary, leak checks, image diff) and the first device, resource, upload and readback cases, against the Vulkan backend | G10, and #55's split up to uploads and textures (G7) | ~700 |

Order: G1, then G2b, then G2, then G10; the Vulkan backend issues follow #55's split once G2b has landed the interface, and C1 starts when both meet.

G3-G12 (#55's backend issues) implement the backend interface above piece by piece; G5 (swapchain) also needs P3's surface source.

## docs/03 3.8 objects

| Object | Where |
|--------|-------|
| `GraphicsDevice` | Sections 1, 2, 4: a sealed front end, owned by its creator |
| `Swapchain` | Section 3: `SwapchainHandle`, created from an `IWindow` |
| `Buffer` | Sections 5, 6: `BufferHandle` |
| `Texture` | Sections 5, 6, 10: `TextureHandle`, including transient targets |
| `TextureView` | Section 5: `TextureViewHandle`, plus a default view per texture |
| `Sampler` | Section 5: `SamplerHandle` |
| `ShaderModule` | Section 7: `ShaderModuleHandle`, from embedded SPIR-V |
| `PipelineLayout` | Section 7: `PipelineLayoutHandle`, declared in C# in M1 |
| `GraphicsPipeline` | Section 7: `GraphicsPipelineHandle` |
| `ComputePipeline` | Section 7: `ComputePipelineHandle` |
| `BindGroup` | Section 8: `BindGroupHandle`, persistent or per frame |
| `BindGroupLayout` | Section 8: `BindGroupLayoutHandle` |
| `CommandList` | Section 9: the open frame's recorder, a value type, not a handle |
| `Fence` | Deferred as a public object: frames in flight and `CompletedFrameNumber` cover every M1 wait (frame slots, readback, timestamps), and WebGPU exposes no fence either. Reconsider with multi-queue or parallel submission |
| `QueryPool` | Deferred as a public object: M1 needs only per-pass timestamps, which the device manages per frame slot (section 11). Occlusion and pipeline-statistics queries have no 2D consumer yet |

## Deviations from the architecture and testing docs

The note is consistent with ADR-0002 (a WebGPU-level interface, Vulkan 1.3 first, Slang compiled offline to SPIR-V, one conformance suite). Where it narrows or departs from docs/03 or docs/05:

1. **No reflection data in M1** (section 7): pipeline layouts are declared in C#, by the shader-bytecode decision (ADR-0012). Reflection returns with M2's cooker.
2. **`Fence` and `QueryPool` aren't public objects** (table above), and **`CommandList` is a per-frame value**, not a handle: the open frame owns its commands.
3. **The frame graph doesn't reorder passes in M1** (section 10): declaration order is the execution order.
4. **Device-lost recreation goes through owner callbacks** (section 12), by the device-loss decision; the asset database becomes an owner in M2.
5. **Swapchain behaviour is tested through the fault-injection seam and real headless or windowed surfaces** (section 15), which is how this note realises docs/05's "mock surface layer".
6. **Thread checks are always on** (section 14), where docs/03 3.2 asks for them in debug builds: a wrong-thread native call in a release build is undefined behaviour, and the check is one integer compare. The platform note makes the same choice.

## Not verified

- The note is design only. Nothing in it has run. M0's proven behaviour (frames in flight, acquire, present, resize, minimise, headless and windowed surfaces) is cited where it is reused.
- Timestamp support on lavapipe is unknown until G11 runs there. On MoltenVK, timestamp support and `FormatSupport` for some formats and usages (for example `Rgba16Float` as a storage texture, and linear filtering of `Depth32Float`) stay unknown: macOS is best-effort and unverified, and CI doesn't run G11 or C1 there ([ADR-0015](../adr/0015-tested-platforms-windows-linux.md)).
- The surface contract is reconciled with the [platform note](platform.md) as written, but neither has been implemented: P1/P3 and G2b/G5 prove it.
