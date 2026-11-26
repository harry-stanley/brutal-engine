using System;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// One device a backend created: it owns its instance, its device and every object it creates, and records and submits
/// the frames the front end plans (docs/design/graphics.md, "The backend interface"). Called only by the graphics
/// device's front end, never by games.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> Created by <see cref="IGraphicsBackend.CreateDevice"/>, then owned by the front end, which disposes it
/// after <see cref="WaitIdle"/> and its leak report, or after a device loss to recover. <see cref="IDisposable.Dispose"/>
/// destroys every object still alive, surfaces included (an unused selection surface too), and works on a lost device.
/// Object ids are non-zero, chosen by the backend and opaque to the front end; mapped pointers stay valid until their
/// buffer is destroyed.
/// </para>
/// <para>
/// <b>Threading.</b> Every member is called on the device's owner thread, never re-entrantly, so an implementation needs
/// no locking. <see cref="WindowSurfaceSource.GetRequiredInstanceExtension"/> is the only member a backend may call from
/// another thread.
/// </para>
/// <para>
/// <b>Errors.</b> Every native status comes back as a <see cref="Result"/> or a status, never as an exception. Device loss is
/// <see cref="ErrorKind.Unavailable"/> with the native code in <see cref="EngineError.NativeCode"/>, or
/// <see cref="SwapchainStatus.DeviceLost"/>. Misuse can't reach a backend: the front end validates first, so a backend may
/// assume valid descriptions, live ids it issued, frame slots below <see cref="BackendDeviceRequest.FramesInFlight"/>, and
/// the call order below. No member throws for a native failure; <see cref="InjectValidationError"/> throws for misuse.
/// </para>
/// <para>
/// <b>Frames.</b> For each frame the front end calls <see cref="WaitForFrame"/> for its slot, then <see cref="BeginFrame"/>,
/// then any number of <see cref="AcquireImage"/> calls, then <see cref="Execute"/>, then <see cref="Present"/> for each
/// acquired image. Any failure of <see cref="WaitForFrame"/>, <see cref="IsFrameComplete"/>, <see cref="BeginFrame"/> or
/// <see cref="Execute"/> is treated as device loss, because a frame call has no partial recovery.
/// </para>
/// <para>
/// <b>Allocation.</b> <see cref="WaitForFrame"/>, <see cref="IsFrameComplete"/>, <see cref="BeginFrame"/>,
/// <see cref="AcquireImage"/>, <see cref="Execute"/>, <see cref="Present"/>, <see cref="TryReadTimestamps"/> and
/// <see cref="Destroy"/> allocate no managed memory. Creation calls may allocate.
/// </para>
/// </remarks>
public interface IBackendDevice : IDisposable
{
    /// <summary>The adapter the device was created on, as <see cref="IGraphicsBackend.EnumerateAdapters"/> reported it.</summary>
    /// <remarks>Immutable after creation. Allocates nothing.</remarks>
    public GraphicsAdapterInfo Adapter { get; }

    /// <summary>The features the device enabled (the required ones plus the optional ones the adapter has) and its limits.</summary>
    /// <remarks>Immutable after creation. Allocates nothing.</remarks>
    public GraphicsCapabilities Capabilities { get; }

    /// <summary>Reports what the device can do with textures of one format.</summary>
    /// <param name="format">A defined format other than <see cref="TextureFormat.Undefined"/>.</param>
    /// <returns>The supported uses; <see cref="FormatSupport.None"/> when the format can't be used at all.</returns>
    /// <remarks>Constant for the device's lifetime. Allocates nothing.</remarks>
    public FormatSupport GetFormatSupport(TextureFormat format);

    /// <summary>
    /// Creates a buffer in the memory <paramref name="memory"/> names, mapped persistently when it is
    /// <see cref="BackendMemory.HostUpload"/> or <see cref="BackendMemory.HostReadback"/>.
    /// </summary>
    /// <param name="description">The buffer's description, already validated.</param>
    /// <param name="memory">Where the buffer lives, chosen by the front end.</param>
    /// <returns>
    /// The buffer's id and, for host memory, its mapped pointer. Fails with <see cref="ErrorKind.OutOfMemory"/> when memory
    /// is exhausted, or <see cref="ErrorKind.Unavailable"/> after device loss.
    /// </returns>
    /// <remarks>
    /// The description's debug name is applied as the object's name. The buffer lives until <see cref="Destroy"/> or
    /// <see cref="IDisposable.Dispose"/>. May allocate.
    /// </remarks>
    public Result<BackendBuffer> CreateBuffer(in BufferDescription description, BackendMemory memory);

    /// <summary>Creates a texture.</summary>
    /// <param name="description">The texture's description, already validated against the device's limits and format support.</param>
    /// <returns>
    /// The texture's id. Fails with <see cref="ErrorKind.OutOfMemory"/> when memory is exhausted, or
    /// <see cref="ErrorKind.Unavailable"/> after device loss.
    /// </returns>
    /// <remarks>The texture lives until <see cref="Destroy"/> or <see cref="IDisposable.Dispose"/>. May allocate.</remarks>
    public Result<ulong> CreateTexture(in TextureDescription description);

    /// <summary>Creates a view of a texture.</summary>
    /// <param name="description">
    /// The view's description, already validated; its texture handle resolves through the device's
    /// <see cref="BackendObjectTable"/>.
    /// </param>
    /// <returns>The view's id, or a failure as for <see cref="CreateTexture"/>.</returns>
    /// <remarks>
    /// The front end destroys every view before its texture. The view lives until <see cref="Destroy"/> or
    /// <see cref="IDisposable.Dispose"/>. May allocate.
    /// </remarks>
    public Result<ulong> CreateTextureView(in TextureViewDescription description);

    /// <summary>Creates a sampler.</summary>
    /// <param name="description">The sampler's description, already validated against the device's features and limits.</param>
    /// <returns>The sampler's id, or a failure as for <see cref="CreateTexture"/>.</returns>
    /// <remarks>The sampler lives until <see cref="Destroy"/> or <see cref="IDisposable.Dispose"/>. May allocate.</remarks>
    public Result<ulong> CreateSampler(in SamplerDescription description);

    /// <summary>Names an object for debugging tools and validation messages.</summary>
    /// <param name="kind">The object's kind.</param>
    /// <param name="id">The object's id, live.</param>
    /// <param name="utf8Name">The name as UTF-8, without a NUL terminator; valid during the call only.</param>
    /// <remarks>
    /// Best effort: does nothing when the backend's debug tooling is off, and a backend may shorten a long name. Never
    /// fails. Allocates nothing.
    /// </remarks>
    public void SetDebugName(BackendObjectKind kind, ulong id, ReadOnlySpan<byte> utf8Name);

    /// <summary>Destroys an object at once.</summary>
    /// <param name="kind">The object's kind.</param>
    /// <param name="id">The object's id, live. Never a swapchain image's texture or view.</param>
    /// <remarks>
    /// The front end calls it only once every frame that used the object has completed, so the backend needn't defer it.
    /// Frame-lifetime bind groups are released by their slot's pool reset and never come here. Never fails. Allocates
    /// nothing.
    /// </remarks>
    public void Destroy(BackendObjectKind kind, ulong id);

    /// <summary>Creates the window's surface, or hands over the one made for adapter selection.</summary>
    /// <param name="source">The window's surface source.</param>
    /// <returns>
    /// The surface's id. For the <see cref="BackendDeviceRequest.CompatibleSurface"/> instance, the first
    /// <see cref="CreateSurface"/> returns the selection surface instead of making a second one. Fails with
    /// <see cref="ErrorKind.Unsupported"/> when the device's instance lacks the window's extensions or its queue can't
    /// present to the surface, or with the source's own failure unchanged.
    /// </returns>
    /// <remarks>
    /// A window has at most one live surface (the platform contract), and the front end creates at most one per window.
    /// The surface lives until <see cref="DestroySurface"/> or <see cref="IDisposable.Dispose"/>. May allocate.
    /// </remarks>
    public Result<ulong> CreateSurface(WindowSurfaceSource source);

    /// <summary>Destroys a surface.</summary>
    /// <param name="surface">The surface's id, live, with no live swapchain on it.</param>
    /// <remarks>Called after <see cref="DestroySwapchain"/> and before the window is disposed. Never fails. May allocate.</remarks>
    public void DestroySurface(ulong surface);

    /// <summary>Creates a swapchain on a surface, or recreates one.</summary>
    /// <param name="request">
    /// The surface, its window, the options and the swapchain being replaced (0 for none). The window isn't minimised.
    /// </param>
    /// <returns>
    /// The new swapchain's id and what it really is. Fails with <see cref="ErrorKind.OutOfMemory"/>,
    /// <see cref="ErrorKind.Unavailable"/> after device loss, or the native code; a failed recreation still retires the old
    /// swapchain.
    /// </returns>
    /// <remarks>
    /// With a non-zero <see cref="BackendSwapchainRequest.OldSwapchain"/> the surface is that swapchain's own, and the old
    /// swapchain is retired. The front end waits for the device to go idle first, then calls <see cref="DestroySwapchain"/>
    /// on the old id whether this succeeded or failed. After each creation the front end reads the images with
    /// <see cref="GetSwapchainImage"/>. May allocate.
    /// </remarks>
    public Result<BackendSwapchain> CreateSwapchain(in BackendSwapchainRequest request);

    /// <summary>Destroys a swapchain and its images' views, never its surface.</summary>
    /// <param name="swapchain">The swapchain's id, live (retired or not).</param>
    /// <remarks>
    /// Called once the swapchain's frames have completed. Its image texture and view ids become invalid. Never fails: a
    /// problem it meets (a hung present) is reported by the next frame call. May allocate.
    /// </remarks>
    public void DestroySwapchain(ulong swapchain);

    /// <summary>Gets the texture and view ids of one swapchain image.</summary>
    /// <param name="swapchain">The swapchain's id, live.</param>
    /// <param name="imageIndex">The image's index, from 0 to the swapchain's image count minus 1.</param>
    /// <param name="texture">The image's texture id.</param>
    /// <param name="view">The image's view id, usable as a color target or, with copy support, a copy destination.</param>
    /// <remarks>
    /// Called after each <see cref="CreateSwapchain"/>. The ids belong to the swapchain: they never go to
    /// <see cref="Destroy"/>, and are invalid after <see cref="DestroySwapchain"/>. Allocates nothing.
    /// </remarks>
    public void GetSwapchainImage(ulong swapchain, int imageIndex, out ulong texture, out ulong view);

    /// <summary>Acquires a swapchain's next image for the frame in a slot.</summary>
    /// <param name="swapchain">The swapchain's id, live and not retired.</param>
    /// <param name="frameSlot">The open frame's slot.</param>
    /// <returns>
    /// The status, the image index when the image was acquired (<see cref="SwapchainStatus.Success"/> or
    /// <see cref="SwapchainStatus.Suboptimal"/>; -1 otherwise), and the native code. A wait that runs out of
    /// <see cref="BackendDeviceRequest.GpuTimeout"/> is <see cref="SwapchainStatus.OutOfDate"/>, not a loss: a healthy GPU
    /// whose compositor holds the images can cause it.
    /// </returns>
    /// <remarks>
    /// Called between <see cref="BeginFrame"/> and <see cref="Execute"/> of that slot. The image becomes usable when its
    /// acquire signals, which <see cref="Execute"/> waits for when the image is in <see cref="FramePlan.Acquired"/>.
    /// Allocates nothing.
    /// </remarks>
    public BackendAcquire AcquireImage(ulong swapchain, int frameSlot);

    /// <summary>Presents an image the last <see cref="Execute"/> left presentable.</summary>
    /// <param name="swapchain">The swapchain's id.</param>
    /// <param name="imageIndex">The image's index, from a successful acquire in that frame.</param>
    /// <returns>
    /// The status and the native code. <see cref="SwapchainStatus.Suboptimal"/> and <see cref="SwapchainStatus.OutOfDate"/>
    /// mark the swapchain for recreation at the next acquire.
    /// </returns>
    /// <remarks>Called after <see cref="Execute"/>, once per image in its plan's <see cref="FramePlan.Acquired"/>. Allocates nothing.</remarks>
    public BackendPresent Present(ulong swapchain, int imageIndex);

    /// <summary>Waits until the frame last submitted from a slot has completed on the GPU.</summary>
    /// <param name="frameSlot">The slot.</param>
    /// <returns>
    /// Success once the frame completed: at once for a slot that never submitted, or whose last <see cref="Execute"/>
    /// failed before submitting. Then the slot's resources are free, and the frame's GPU writes to host memory are visible
    /// to the CPU. <see cref="ErrorKind.Timeout"/> after <see cref="BackendDeviceRequest.GpuTimeout"/>, which the front end
    /// reports as a hung GPU; any other failure carries its native code.
    /// </returns>
    /// <remarks>Blocks for at most the GPU timeout. Allocates nothing.</remarks>
    public Result WaitForFrame(int frameSlot);

    /// <summary>Reports whether the frame last submitted from a slot has completed, without waiting.</summary>
    /// <param name="frameSlot">The slot.</param>
    /// <returns>
    /// <see langword="true"/> once the frame completed (or for a slot with nothing pending), with the same visibility as
    /// <see cref="WaitForFrame"/>; <see langword="false"/> while it runs. A failure carries its native code.
    /// </returns>
    /// <remarks>Never blocks. Allocates nothing.</remarks>
    public Result<bool> IsFrameComplete(int frameSlot);

    /// <summary>Opens a slot for a new frame: resets its command, descriptor and query pools.</summary>
    /// <param name="frameSlot">The slot, whose <see cref="WaitForFrame"/> has just succeeded.</param>
    /// <returns>Success, or a failure with its native code (for example a failed pool reset).</returns>
    /// <remarks>Releases the slot's frame-lifetime bind groups. Allocates nothing.</remarks>
    public Result BeginFrame(int frameSlot);

    /// <summary>Records and submits a planned frame. Presents come after, through <see cref="Present"/>.</summary>
    /// <param name="plan">The frame's plan, valid during the call only.</param>
    /// <returns>
    /// Success once the frame is submitted, or a failure with its native code (for example out of memory at submit).
    /// </returns>
    /// <remarks>
    /// <para>
    /// The submission waits for every image in <see cref="FramePlan.Acquired"/>, leaves each presentable, and signals the
    /// slot's completion. CPU writes to host memory made before the call are visible to the frame's GPU work, and the
    /// frame's GPU writes to <see cref="BackendMemory.HostReadback"/> buffers get a host-read memory dependency before the
    /// completion signal, so they are visible once the frame is reported complete.
    /// </para>
    /// <para>
    /// An <see cref="Execute"/> that fails before submitting leaves the slot's completion signalled, so the slot's next
    /// <see cref="WaitForFrame"/> returns at once. Allocates nothing.
    /// </para>
    /// </remarks>
    public Result Execute(in FramePlan plan);

    /// <summary>Reads the raw timestamps the slot's last frame wrote, without waiting.</summary>
    /// <param name="frameSlot">The slot.</param>
    /// <param name="ticks">
    /// Receives one tick count per query index the front end assigned in that frame (two per labelled pass), from index 0.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the results were written; <see langword="false"/> until the slot's frame completed, when
    /// <see cref="GraphicsFeatures.TimestampQueries"/> isn't enabled, or when the device is lost (the next frame call
    /// reports it).
    /// </returns>
    /// <remarks>
    /// The front end converts ticks to time with <see cref="GraphicsLimits.TimestampPeriodNanoseconds"/>. Never blocks.
    /// Allocates nothing.
    /// </remarks>
    public bool TryReadTimestamps(int frameSlot, Span<ulong> ticks);

    /// <summary>Waits until the device has finished all submitted work.</summary>
    /// <returns>Success, or a failure with its native code (<see cref="ErrorKind.Unavailable"/> after device loss).</returns>
    /// <remarks>
    /// For tests, teardown and swapchain recreation; never in steady frames. Blocks for at most
    /// <see cref="BackendDeviceRequest.GpuTimeout"/> per wait. May allocate.
    /// </remarks>
    public Result WaitIdle();

    /// <summary>The number of error messages the validation layer has reported for this device; 0 without validation.</summary>
    /// <remarks>Only grows. Allocates nothing.</remarks>
    public long ValidationErrors { get; }

    /// <summary>The number of warning messages the validation layer has reported for this device; 0 without validation.</summary>
    /// <remarks>Only grows. Allocates nothing.</remarks>
    public long ValidationWarnings { get; }

    /// <summary>
    /// Test only, the negative canary: makes the next <see cref="Execute"/> record one known validation violation, so a
    /// test that sees <see cref="ValidationErrors"/> grow proves the validation layer is loaded.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The device was created without <see cref="BackendDeviceRequest.Validation"/>: with no layer to catch it, the violation
    /// would be undefined behaviour on real hardware.
    /// </exception>
    /// <remarks>Reached through the front end's internal test seam only. May allocate.</remarks>
    public void InjectValidationError();
}
