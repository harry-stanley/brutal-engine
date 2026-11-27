using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Platform;

namespace BrutalEngine.Graphics;

/// <content>Swapchain creation and destruction (docs/design/graphics.md section 3).</content>
public sealed partial class GraphicsDevice
{
    private readonly HandleAllocator _swapchainHandles = new();
    private readonly List<RetiringSwapchain> _retiring = [];
    private readonly IWindow? _compatibleWindow;
    private readonly WindowSurfaceSource? _compatibleSurface;
    private readonly List<SwapchainImageUse> _acquiredUses = [];
    private readonly List<AcquiredImage> _acquiredImages = [];
    private SwapchainState?[] _swapchains = [];

    /// <summary>Creates the swapchain a window is presented through.</summary>
    /// <param name="window">
    /// The window to present to: the device's compatible window, or another window of the same platform. Not owned; it must
    /// outlive the swapchain.
    /// </param>
    /// <param name="options">The present mode, format kind and debug name; checked first.</param>
    /// <returns>
    /// The swapchain's handle; the device owns the swapchain and its surface until <see cref="Destroy(SwapchainHandle)"/>
    /// or <see cref="Dispose"/>. Fails with <see cref="ErrorKind.Unsupported"/> for a window created with
    /// <c>WindowSurface.None</c> or on a device created without a compatible window (offscreen-only), and with
    /// <see cref="ErrorKind.Cancelled"/> while the window is minimised or has a zero side (create it once it is restored).
    /// A backend failure is returned unchanged: the surface's (for example <see cref="ErrorKind.Unsupported"/> for a window
    /// the device can't present to, or <see cref="ErrorKind.Native"/> from the platform), or the swapchain's
    /// (<see cref="ErrorKind.Cancelled"/> for a surface extent of zero, <see cref="ErrorKind.NotFound"/> for a lost surface,
    /// <see cref="ErrorKind.OutOfMemory"/>, or <see cref="ErrorKind.Unavailable"/> after device loss), or the failure of
    /// the wait described below.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="window"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="SwapchainOptions.PresentMode"/> or <see cref="SwapchainOptions.Format"/> isn't a member of its enum.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The window already has a live swapchain (one per window), or its previous swapchain was destroyed in a frame that
    /// hasn't been submitted yet (the open frame, or the next one while writes are pending): submit that frame first.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// The device's handle tables couldn't grow; the new swapchain and its surface are destroyed. Also
    /// <see cref="InvalidOperationException"/> when a handle allocator is exhausted, with the same cleanup.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device or the window has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// <para>
    /// The compatible window's first swapchain gets the surface the backend created for adapter selection; any other
    /// swapchain gets a new surface. If the backend can't create the swapchain, its surface (the selection surface too) is
    /// destroyed at once, so a retry never makes a second live surface for the window.
    /// </para>
    /// <para>
    /// When the window's previous swapchain is still waiting for its frames after <see cref="Destroy(SwapchainHandle)"/>,
    /// the call first waits for the GPU to go idle (<see cref="WaitIdle"/>), so the old swapchain and surface are gone
    /// before the new ones exist. Each swapchain image is registered as a texture whose default view is the image's view,
    /// owned by the swapchain: they are never counted in <see cref="Statistics"/> or the leak report, and
    /// <c>Destroy</c> rejects them. Owner thread. Allocates.
    /// </para>
    /// </remarks>
    public Result<SwapchainHandle> CreateSwapchain(IWindow window, SwapchainOptions options)
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(CreateSwapchain));
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(options);
        if ((uint)options.PresentMode > (uint)PresentMode.Immediate || (uint)options.Format > (uint)SwapchainFormat.Unorm)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options, "CreateSwapchain: the present mode or format isn't a member of its enum.");
        }

        if (window.VulkanSurface is not { } surfaceSource)
        {
            return new EngineError(ErrorKind.Unsupported, "CreateSwapchain: the window was created with WindowSurface.None.");
        }

        if (_compatibleSurface is null)
        {
            return new EngineError(ErrorKind.Unsupported, "CreateSwapchain: the device is offscreen-only (created without a compatible window).");
        }

        if (Array.Find(_swapchains, live => ReferenceEquals(live?.Window, window)) is { } existing)
        {
            throw new InvalidOperationException($"CreateSwapchain: the window already has the live swapchain '{existing.Options.DebugName}'; destroy it first.");
        }

        WindowSurfaceSource source = ReferenceEquals(window, _compatibleWindow) ? _compatibleSurface : new WindowSurfaceSource(window, surfaceSource);
        source.Refresh();
        if (source.IsMinimised)
        {
            return new EngineError(ErrorKind.Cancelled, "CreateSwapchain: the window is minimised or has a zero side.");
        }

        Result waited = WaitForRetiredSwapchain(window);
        if (waited.TryGetError(out EngineError error))
        {
            return error;
        }

        Result<ulong> createdSurface = _backend.CreateSurface(source);
        if (!createdSurface.TryGetValue(out ulong surface))
        {
            return createdSurface.Error;
        }

        Result<BackendSwapchain> created = _backend.CreateSwapchain(new BackendSwapchainRequest(surface, source, options, OldSwapchain: 0));
        if (!created.TryGetValue(out BackendSwapchain swapchain))
        {
            _backend.DestroySurface(surface);
            return created.Error;
        }

        try
        {
            return new SwapchainHandle(Register(window, source, surface, swapchain, options));
        }
        catch
        {
            // The tables couldn't grow: nothing refers to the new swapchain.
            _backend.DestroySwapchain(swapchain.Id);
            _backend.DestroySurface(surface);
            throw;
        }
    }

    /// <summary>Reports what a swapchain really is: its size, format, present mode, image count and copy support.</summary>
    /// <param name="swapchain">A live swapchain of this device.</param>
    /// <returns>The swapchain as the backend created it.</returns>
    /// <exception cref="ArgumentException"><paramref name="swapchain"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException"><paramref name="swapchain"/> isn't a live swapchain of this device.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public SwapchainInfo GetSwapchainInfo(SwapchainHandle swapchain)
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(GetSwapchainInfo));
        return LiveSwapchain(swapchain, nameof(swapchain)).Info;
    }

    /// <summary>Returns one image of a live swapchain: the handles its acquire hands out, and its size and format.</summary>
    /// <param name="swapchain">A live swapchain of this device.</param>
    /// <param name="imageIndex">The image's index, from 0 to the image count minus 1.</param>
    /// <returns>The image's texture and view handles, owned by the swapchain, with the swapchain's size and format.</returns>
    /// <exception cref="ArgumentException"><paramref name="swapchain"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException"><paramref name="swapchain"/> isn't a live swapchain of this device.</exception>
    /// <exception cref="IndexOutOfRangeException"><paramref name="imageIndex"/> is outside the swapchain's images.</exception>
    internal SwapchainImage GetSwapchainImage(SwapchainHandle swapchain, int imageIndex) =>
        LiveSwapchain(swapchain, nameof(swapchain)).Images[imageIndex];

    /// <summary>The device side of <see cref="CommandList.AcquireSwapchainImage"/>; the list's documentation is the contract.</summary>
    internal AcquireStatus AcquireSwapchainImage(CommandList commands, SwapchainHandle swapchain, out SwapchainImage image)
    {
        const string Api = nameof(CommandList) + "." + nameof(CommandList.AcquireSwapchainImage);
        ThrowIfUnusable(Api);
        ThrowIfNotOpenFrame(commands, Api);
        SwapchainState state = LiveSwapchain(swapchain, nameof(swapchain));
        if (state.AcquiredFrame == _frameNumber)
        {
            throw new InvalidOperationException($"{Api}: swapchain '{state.Options.DebugName}' already has an image acquired in frame {_frameNumber}.");
        }

        image = default;
        if (IsLostCore || state.SurfaceLost)
        {
            return IsLostCore ? AcquireStatus.DeviceLost : AcquireStatus.SurfaceLost;
        }

        state.Source.Refresh();
        if (state.Source.IsMinimised)
        {
            return AcquireStatus.Minimised;
        }

        // Recreate only for a real size change (SDL reports a resize right after creation), a marked or missing swapchain.
        bool recreated = state.Id == 0 || state.NeedsRecreation || state.CreatedWidth != state.Source.PixelWidth || state.CreatedHeight != state.Source.PixelHeight;
        AcquireStatus status = recreated ? Recreate(state) : AcquireStatus.Recreated;
        if (status != AcquireStatus.Recreated)
        {
            return status;
        }

        BackendAcquire acquired = _backend.AcquireImage(state.Id, CurrentSlot());
        if (acquired.Status == SwapchainStatus.OutOfDate)
        {
            // Out of date (an acquire timeout too): recreate and retry once; still out of date skips the frame.
            (recreated, status) = (true, Recreate(state));
            if (status != AcquireStatus.Recreated)
            {
                return status;
            }

            acquired = _backend.AcquireImage(state.Id, CurrentSlot());
            if (acquired.Status == SwapchainStatus.OutOfDate)
            {
                return AcquireStatus.Skipped;
            }
        }

        if (acquired.Status is SwapchainStatus.SurfaceLost or SwapchainStatus.DeviceLost)
        {
            return Lost(state, acquired.Status, new EngineError(ErrorKind.Unavailable, "AcquireImage reported device loss.", acquired.NativeCode), nameof(CommandList.AcquireSwapchainImage));
        }

        state.NeedsRecreation = acquired.Status == SwapchainStatus.Suboptimal;
        state.AcquiredFrame = _frameNumber;
        image = state.Images[acquired.ImageIndex];
        _acquiredUses.Add(new SwapchainImageUse(state.Id, acquired.ImageIndex));
        _acquiredImages.Add(new AcquiredImage(state, _handles.GetId(BackendObjectKind.Texture, image.Texture.Value, nameof(image))));
        return recreated ? AcquireStatus.Recreated : AcquireStatus.Acquired;
    }

    /// <summary>Destroys a swapchain and its surface: the handle, its images' handles and every copy become stale at once.</summary>
    /// <param name="swapchain">A live swapchain of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="swapchain"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="swapchain"/> isn't a live swapchain of this device: it was destroyed already, or was never issued
    /// here. A handle from another device is detected only when its index or generation doesn't match a live swapchain
    /// here; one that matches can't be told apart from it.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// A deletion queue or the list of destroyed swapchains couldn't grow; nothing changed and the swapchain stays live.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// The backend swapchain, then its surface, are destroyed once the frame that may still use them has completed, through
    /// that frame's deletion queue: the open frame, else the last submitted one (or the next one while writes are pending).
    /// Before the first frame begins, they are destroyed at once. A later <see cref="CreateSwapchain"/> for the same window
    /// waits for them. Owner thread. May allocate: the list of destroyed swapchains waiting for a frame can grow.
    /// </remarks>
    public void Destroy(SwapchainHandle swapchain)
    {
        ThrowIfUnusable(DestroyApi);
        SwapchainState state = LiveSwapchain(swapchain, nameof(swapchain));
        // Drop the records already retired, then make room for this one first: an out-of-memory failure must leave
        // everything as it was.
        PruneRetiring();
        _ = _retiring.EnsureCapacity(_retiring.Count + 1);
        DeletionQueue? queue = ReserveDeletion();
        _swapchainHandles.Free(swapchain.Value);
        _swapchains[swapchain.Value.Index] = null;
        TearDown(state, queue);
        if (queue is not null)
        {
            // The queue is retired once this frame has completed: the deferred queue goes to the next frame.
            _retiring.Add(new RetiringSwapchain(state.Window, _frameOpen || ReferenceEquals(queue, _deferred) ? _frameNumber : _frameNumber - 1));
        }
    }

    // Adds a "Swapchain 'name'" line per live swapchain, with its creation order, for the leak report.
    private void AddSwapchainLeaks(List<(long Sequence, string Line)> lines)
    {
        foreach (SwapchainState? state in _swapchains)
        {
            if (state is not null)
            {
                lines.Add((state.Sequence, $"Swapchain '{state.Options.DebugName}'"));
            }
        }
    }

    // At Dispose, once the GPU is idle: every live swapchain, newest first (the empty slots sort last), then its surface.
    private void DestroySwapchains()
    {
        Array.Sort(_swapchains, static (a, b) => (b?.Sequence ?? -1).CompareTo(a?.Sequence ?? -1));
        foreach (SwapchainState? state in _swapchains)
        {
            if (state is not null)
            {
                TearDown(state, queue: null);
            }
        }

        _swapchainHandles.Clear();
        Array.Clear(_swapchains);
    }

    // Ends the images' handles, then destroys the swapchain and its surface at once, or queues both in that order (the
    // queue skips the swapchain id 0 a failed recreation leaves, as the immediate path does).
    private void TearDown(SwapchainState state, DeletionQueue? queue)
    {
        foreach (SwapchainImage image in state.Images)
        {
            ReleaseImage(image);
        }

        if (queue is not null)
        {
            queue.AddSwapchain(state.Id, state.Surface);
            return;
        }

        // A failed recreation leaves no swapchain, only the surface.
        if (state.Id != 0)
        {
            _backend.DestroySwapchain(state.Id);
        }

        _backend.DestroySurface(state.Surface);
    }

    // Records a created swapchain under a new handle, with its images as textures and views it owns.
    private Handle Register(IWindow window, WindowSurfaceSource source, ulong surface, in BackendSwapchain swapchain, SwapchainOptions options)
    {
        Handle handle = _swapchainHandles.Allocate();
        try
        {
            if (handle.Index >= (uint)_swapchains.Length)
            {
                Array.Resize(ref _swapchains, Math.Max(_swapchains.Length * 2, (int)handle.Index + 1));
            }

            var state = new SwapchainState(window, source, surface, options, _handles.NextSequence());
            RegisterImages(state, swapchain);
            _swapchains[handle.Index] = state;
            return handle;
        }
        catch
        {
            _swapchainHandles.Free(handle);
            throw;
        }
    }

    // Makes a created swapchain the state's own: its id and info, the window size it was made for, and its images as
    // textures and views it owns. If the tables can't grow, nothing stays registered and the state has no swapchain.
    private void RegisterImages(SwapchainState state, in BackendSwapchain swapchain)
    {
        SwapchainInfo info = swapchain.Info;
        var description = new TextureDescription
        {
            Format = info.Format,
            Width = info.Width,
            Height = info.Height,
            Usage = TextureUsage.ColorTarget | (info.SupportsCopyDestination ? TextureUsage.CopyDestination : TextureUsage.None),
            DebugName = state.Options.DebugName,
        };
        var images = new SwapchainImage[info.ImageCount];
        int registered = 0;
        try
        {
            for (; registered < images.Length; registered++)
            {
                _backend.GetSwapchainImage(swapchain.Id, registered, out ulong textureId, out ulong viewId);
                (Handle texture, Handle view) = _handles.AddSwapchainImage(textureId, viewId, description);
                images[registered] = new SwapchainImage(new TextureHandle(texture), new TextureViewHandle(view), info.Width, info.Height, info.Format);
            }
        }
        catch
        {
            for (int i = 0; i < registered; i++)
            {
                ReleaseImage(images[i]);
            }

            throw;
        }

        (state.Id, state.Info, state.Images) = (swapchain.Id, info, images);
        (state.CreatedWidth, state.CreatedHeight) = (state.Source.PixelWidth, state.Source.PixelHeight);
    }

    // Recreation, as M0 does it: wait for the GPU, create from the old swapchain, destroy the old one whatever the result.
    // A failure leaves the state without a swapchain, so the next acquire creates one with no old swapchain.
    private AcquireStatus Recreate(SwapchainState state)
    {
        if (WaitIdle().TryGetError(out EngineError error))
        {
            return RecreationFailed(state, error);
        }

        ulong old = state.Id;
        foreach (SwapchainImage image in state.Images)
        {
            ReleaseImage(image);
        }

        (state.Id, state.Images) = (0, []);
        Result<BackendSwapchain> created = _backend.CreateSwapchain(new BackendSwapchainRequest(state.Surface, state.Source, state.Options, old));
        if (old != 0)
        {
            _backend.DestroySwapchain(old);
        }

        if (!created.TryGetValue(out BackendSwapchain swapchain))
        {
            return RecreationFailed(state, created.Error);
        }

        try
        {
            RegisterImages(state, swapchain);
        }
        catch
        {
            _backend.DestroySwapchain(swapchain.Id);
            throw;
        }

        state.NeedsRecreation = false;
        return AcquireStatus.Recreated;
    }

    // Maps a failed recreation (the contract's swapchain-creation row): device loss, a lost surface, a zero extent, or else a
    // skipped frame that is logged.
    private AcquireStatus RecreationFailed(SwapchainState state, in EngineError error)
    {
        switch (error.Kind)
        {
            case ErrorKind.Unavailable or ErrorKind.Timeout:
                return Lost(state, SwapchainStatus.DeviceLost, error, nameof(CommandList.AcquireSwapchainImage));
            case ErrorKind.NotFound:
                return Lost(state, SwapchainStatus.SurfaceLost, error, nameof(CommandList.AcquireSwapchainImage));
            case ErrorKind.Cancelled:
                return AcquireStatus.Minimised;
            default:
                _log.Error($"Recreating swapchain '{state.Options.DebugName}' failed ({error.Kind}, native code {error.NativeCode}): {error.Message}. The next acquire tries again.");
                return AcquireStatus.Skipped;
        }
    }

    // Records a lost surface on the swapchain (every later acquire reports it) or a lost device on the device.
    private AcquireStatus Lost(SwapchainState state, SwapchainStatus status, in EngineError error, string operation)
    {
        if (status == SwapchainStatus.SurfaceLost)
        {
            state.SurfaceLost = true;
            return AcquireStatus.SurfaceLost;
        }

        if (!IsLostCore)
        {
            MarkLost(operation, error);
        }

        return AcquireStatus.DeviceLost;
    }

    // At submit, before planning: a clear record for each acquired image no recorded copy wrote, then the plan with the
    // acquired list. Allocates nothing once the stream has grown.
    private FramePlan PlanFrame(int slotIndex, FrameSlot slot)
    {
        for (int i = 0; i < _acquiredImages.Count; i++)
        {
            ulong texture = _acquiredImages[i].Texture;
            bool written = false;
            var reader = new CommandReader(slot.Commands.Written);
            while (!written && reader.MoveNext())
            {
                written = reader.Kind switch
                {
                    CommandKind.CopyBufferToTexture => reader.Payload<CopyBufferTextureCommand>().Texture == texture,
                    CommandKind.CopyTextureToTexture => reader.Payload<CopyTextureToTextureCommand>().Destination == texture,
                    _ => false,
                };
            }

            if (!written)
            {
                slot.Commands.Append(CommandKind.ClearSwapchainImage, new ClearSwapchainImageCommand { AcquiredIndex = i });
            }
        }

        FramePlan plan = _planner.Plan(slotIndex, _frameNumber, slot.Uploads, slot.Commands);
        return new FramePlan(plan.FrameSlot, plan.FrameNumber, plan.Commands, CollectionsMarshal.AsSpan(_acquiredUses));
    }

    // After Execute: presents every acquired image in acquire order (unless the frame wasn't submitted or the device is
    // lost), marks swapchains for recreation or as surface-lost, and empties the acquired lists.
    private FrameStatus PresentAcquired(bool submitted)
    {
        for (int i = 0; submitted && !IsLostCore && i < _acquiredUses.Count; i++)
        {
            SwapchainState state = _acquiredImages[i].State;
            BackendPresent presented = _backend.Present(_acquiredUses[i].Swapchain, _acquiredUses[i].ImageIndex);
            state.NeedsRecreation |= presented.Status is SwapchainStatus.Suboptimal or SwapchainStatus.OutOfDate;
            if (presented.Status is SwapchainStatus.SurfaceLost or SwapchainStatus.DeviceLost)
            {
                _ = Lost(state, presented.Status, new EngineError(ErrorKind.Unavailable, "Present reported device loss.", presented.NativeCode), nameof(SubmitFrame));
            }
        }

        _acquiredUses.Clear();
        _acquiredImages.Clear();
        return submitted && !IsLostCore ? FrameStatus.Ready : FrameStatus.DeviceLost;
    }

    // Ends an image's two handles; the backend objects are the swapchain's, destroyed with it. An image has no other
    // view: CreateTextureView rejects swapchain images.
    private void ReleaseImage(in SwapchainImage image)
    {
        _ = _handles.Remove(BackendObjectKind.TextureView, image.View.Value, nameof(image));
        _ = _handles.Remove(BackendObjectKind.Texture, image.Texture.Value, nameof(image));
    }

    // Before a window gets a new surface, its destroyed swapchain and surface must be gone: wait for the GPU when they
    // still wait for a submitted frame; a frame not yet submitted can't be waited for.
    private Result WaitForRetiredSwapchain(IWindow window)
    {
        PruneRetiring();
        long frame = -1;
        foreach (RetiringSwapchain retired in _retiring)
        {
            frame = ReferenceEquals(retired.Window, window) ? Math.Max(frame, retired.Frame) : frame;
        }

        if (frame < 0)
        {
            return Result.Success;
        }

        if (frame >= _frameNumber)
        {
            throw new InvalidOperationException(
                $"CreateSwapchain: the window's previous swapchain waits for frame {frame}, which hasn't been submitted; submit it first.");
        }

        // Success retires every submitted frame's queue, so the record goes at the next call.
        return WaitIdle();
    }

    /// <summary>The destroyed swapchains whose deletion queue hasn't been retired yet, as last pruned; for tests.</summary>
    internal int RetiringSwapchainCount => _retiring.Count;

    // Drops the records of destroyed swapchains whose frame has completed (their queue is retired), so the list stays
    // bounded by the frames in flight however many swapchains are destroyed.
    private void PruneRetiring()
    {
        for (int i = _retiring.Count - 1; i >= 0; i--)
        {
            if (_retiring[i].Frame <= _completedFrameNumber)
            {
                _retiring.RemoveAt(i);
            }
        }
    }

    private SwapchainState LiveSwapchain(SwapchainHandle swapchain, string parameterName)
    {
        if (swapchain.IsNull)
        {
            throw new ArgumentException("The Swapchain handle is null.", parameterName);
        }

        return _swapchainHandles.IsValid(swapchain.Value) ? _swapchains[swapchain.Value.Index]! : throw new StaleHandleException(swapchain.Value);
    }

    /// <summary>
    /// A live swapchain: its window, surface and creation order, and what recreation changes: the backend id (0 after a failed
    /// recreation), what it is, its images, the window size it was made for, and the marks acquires and presents leave.
    /// </summary>
    private sealed class SwapchainState(IWindow window, WindowSurfaceSource source, ulong surface, SwapchainOptions options, long sequence)
    {
        public IWindow Window { get; } = window;

        public WindowSurfaceSource Source { get; } = source;

        public ulong Surface { get; } = surface;

        public SwapchainOptions Options { get; } = options;

        public long Sequence { get; } = sequence;

        public ulong Id { get; set; }

        public SwapchainInfo Info { get; set; }

        public SwapchainImage[] Images { get; set; } = [];

        public int CreatedWidth { get; set; }

        public int CreatedHeight { get; set; }

        public bool NeedsRecreation { get; set; }

        public bool SurfaceLost { get; set; }

        public long AcquiredFrame { get; set; } = -1;
    }

    /// <summary>An image acquired in the open frame: its swapchain, and its texture's backend id for the clear scan.</summary>
    private readonly record struct AcquiredImage(SwapchainState State, ulong Texture);

    /// <summary>A destroyed swapchain still in a deletion queue, retired once <paramref name="Frame"/> has completed.</summary>
    private readonly record struct RetiringSwapchain(IWindow Window, long Frame);
}
