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
    public TextureViewDimension Dimension { get; init; }         // Texture2D, Texture2DArray
    public int BaseMipLevel { get; init; }
    public int MipLevelCount { get; init; } = 1;
    public int BaseArrayLayer { get; init; }
    public int ArrayLayerCount { get; init; } = 1;
    public string? DebugName { get; init; }
}

public readonly record struct SamplerDescription
{
    public Filter MinFilter { get; init; }                       // Nearest (default), Linear
    public Filter MagFilter { get; init; }
    public Filter MipmapFilter { get; init; }
    public AddressMode AddressU { get; init; }                   // ClampToEdge (default), Repeat, MirrorRepeat, ClampToBorder
    public AddressMode AddressV { get; init; }
    public BorderColor BorderColor { get; init; }                // TransparentBlack, OpaqueBlack, OpaqueWhite
    public float MaxAnisotropy { get; init; } = 1;               // > 1 needs GraphicsFeatures.SamplerAnisotropy
    public CompareFunction? Compare { get; init; }               // depth-compare samplers
    public string? DebugName { get; init; }
}
```

The texture formats are the set every M1 consumer needs (sprites, render targets, the overlay, conformance), plus the two depth formats. `Depth16Unorm` is guaranteed as a depth attachment by the Vulkan specification; `Depth32Float` is near-universal but checked like any other format. Depth-stencil formats (stencil masking for UI, M3), 3D and cube textures are added when a consumer needs them. Nearest filtering is the default because pixel-art is the common 2D case.

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

public readonly record struct TextureWrite
{
    public required TextureHandle Texture { get; init; }
    public int MipLevel { get; init; }
    public int ArrayLayer { get; init; }
    public required TextureRegion Region { get; init; }          // X, Y, Width, Height in texels
    public int BytesPerRow { get; init; }                        // 0 = tightly packed
}

public readonly struct CommandList
{
    public TransientAllocation AllocateTransient(long byteCount, TransientUsage usage);
    public void CopyBufferToBuffer(in BufferCopy copy);
    public void CopyTextureToBuffer(in TextureBufferCopy copy);
    public void CopyBufferToTexture(in TextureBufferCopy copy);
    public void CopyTextureToTexture(in TextureCopy copy);      // same format and size; no scaling in M1
}

public readonly ref struct TransientAllocation
{
    public BufferHandle Buffer { get; }      // owned by the device; valid in this frame only
    public long Offset { get; }              // aligned for the usage (uniform: MinUniformBufferOffsetAlignment)
    public Span<byte> Data { get; }          // CPU-writable until SubmitFrame
}

[Flags] public enum TransientUsage { Vertex = 1, Index = 2, Uniform = 4, Storage = 8, Indirect = 16 }
```

- **Write semantics (the same as WebGPU's `queue.writeBuffer`):** `WriteBuffer` and `WriteTexture` copy `data` into the ring at once, so the caller's span can be reused when the call returns. The GPU copies run at the start of the next submitted frame, before any of its passes, and the frame planner orders them after every earlier frame's use of the destination (section 10). So a write is visible to every pass of the next frame, never to work already submitted, and never races a frame in flight. Writes made between frames (loading) go into the next frame. To give two passes of one frame different data, use two buffers or transient allocations.
- **Transient allocations** are for data rebuilt every frame (sprite instances, overlay vertices, per-frame uniforms): the CPU writes straight into ring memory that the GPU reads in the same frame, without a copy. They can be bound with an offset as vertex, index or indirect buffers, or in a frame bind group (section 8). Using one after its frame was submitted is a stale handle.
- **Overflow:** the ring holds `UploadRingBytesPerFrame` per slot. A write or allocation that doesn't fit gets a dedicated block for that frame only, freed with the frame's deletion queue, counted in `GraphicsStatistics.UploadOverflowBytes` and logged at most once per frame at `Warning`. If that block can't be allocated the call returns `ErrorKind.OutOfMemory` and nothing was written. Overflow costs one GPU allocation; it never stalls or drops data.
- **Readback** (needed by conformance): copy into a `Readback` buffer in a frame, then `TryReadBuffer` returns `false` until `CompletedFrameNumber` reaches the frame that last wrote it, and copies the bytes once it has. It never waits. Tests that need the result now call `WaitIdle()` first. Out-of-range reads or a non-`Readback` buffer throw `ArgumentException`.
- **Validation:** offsets and sizes must lie inside the destination, `TextureWrite` regions inside the mip level, `data` must be at least `BytesPerRow * Height` bytes, and the destination must have `CopyDestination` usage; otherwise `ArgumentException`.

## 7. Shaders and pipelines

By decision (maintainer, 2026-11-26; ADR-0012, #52), M1 shaders are Slang sources cooked offline to SPIR-V by `tools/BrutalEngine.Cook`, committed, and embedded as resources in the consuming assembly. The runtime never parses shader source (docs/03 3.8).

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
    public PrimitiveTopology Topology { get; init; }                 // TriangleList (default), TriangleStrip, LineList, LineStrip
    public RasterState Raster { get; init; }                         // CullMode None (default), FrontFace CounterClockwise
    public DepthState? Depth { get; init; }                          // Format, Write, Compare; no stencil in M1
    public required ReadOnlyMemory<ColorTargetState> ColorTargets { get; init; } // Format, Blend?, WriteMask
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
// VertexFormat: Float32, Float32x2, Float32x3, Float32x4, Float16x2, Float16x4, Unorm8x4, Uint8x4, Uint32, Uint32x2, Sint32.
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
    public static BindGroupEntry Buffer(int binding, BufferHandle buffer, long offset = 0, long size = 0); // 0 = to the end
    public static BindGroupEntry Transient(int binding, in TransientAllocation allocation, long size);
    public static BindGroupEntry Texture(int binding, TextureViewHandle view, int arrayElement = 0);
    public static BindGroupEntry Sampler(int binding, SamplerHandle sampler, int arrayElement = 0);
}

public enum BindGroupLifetime { Persistent, Frame }
```

- **Immutable.** A bind group never changes after creation. To bind different resources, create another group. That keeps the frame planner's resource-usage tracking exact (section 10) and maps to every backend.
- **Persistent groups** live until destroyed: material textures, samplers, long-lived buffers. Creating one allocates (a copy of its entries for usage tracking) and belongs to load time.
- **Frame groups** are valid only in the frame they were created in and are released with it. They are created in the frame loop with no managed allocation (the description is a `ref struct` over a span), and are the way to bind transient allocations. The backend serves them from per-frame pools reset with the frame slot (#55). Creating one outside an open frame throws `InvalidOperationException`; using one in a later frame throws `StaleHandleException`.
- **Validation** against the layout: every binding filled exactly once with a resource of the declared type and the needed usage (`Uniform`, `Storage`, `Sampled`, ...), uniform offsets aligned to `MinUniformBufferOffsetAlignment`. Violations throw `ArgumentException`. Layouts reject duplicate binding numbers and `Count < 1`.
