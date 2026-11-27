using System;
using System.Collections.Generic;
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

    /// <summary>Destroys a swapchain and its surface: the handle, its images' handles and every copy become stale at once.</summary>
    /// <param name="swapchain">A live swapchain of this device.</param>
    /// <exception cref="ArgumentException"><paramref name="swapchain"/> is the null handle.</exception>
    /// <exception cref="StaleHandleException">
    /// <paramref name="swapchain"/> isn't a live swapchain of this device: it was destroyed already, or was never issued
    /// here. A handle from another device is detected only when its index or generation doesn't match a live swapchain
    /// here; one that matches can't be told apart from it.
    /// </exception>
    /// <exception cref="OutOfMemoryException">A deletion queue couldn't grow; nothing changed and the swapchain stays live.</exception>
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

    // Ends the images' handles, then destroys the swapchain and its surface at once, or queues both in that order.
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

        _backend.DestroySwapchain(state.Id);
        _backend.DestroySurface(state.Surface);
    }

    // Records a created swapchain under a new handle, with its images as textures and views it owns.
    private Handle Register(IWindow window, WindowSurfaceSource source, ulong surface, in BackendSwapchain swapchain, SwapchainOptions options)
    {
        SwapchainInfo info = swapchain.Info;
        var description = new TextureDescription
        {
            Format = info.Format,
            Width = info.Width,
            Height = info.Height,
            Usage = TextureUsage.ColorTarget | (info.SupportsCopyDestination ? TextureUsage.CopyDestination : TextureUsage.None),
            DebugName = options.DebugName,
        };
        var images = new SwapchainImage[info.ImageCount];
        int registered = 0;
        Handle handle = _swapchainHandles.Allocate();
        try
        {
            if (handle.Index >= (uint)_swapchains.Length)
            {
                Array.Resize(ref _swapchains, Math.Max(_swapchains.Length * 2, (int)handle.Index + 1));
            }

            for (; registered < images.Length; registered++)
            {
                _backend.GetSwapchainImage(swapchain.Id, registered, out ulong textureId, out ulong viewId);
                (Handle texture, Handle view) = _handles.AddSwapchainImage(textureId, viewId, description);
                images[registered] = new SwapchainImage(new TextureHandle(texture), new TextureViewHandle(view), info.Width, info.Height, info.Format);
            }

            _swapchains[handle.Index] = new SwapchainState(window, source, surface, swapchain.Id, info, options, images, _handles.NextSequence());
            return handle;
        }
        catch
        {
            for (int i = 0; i < registered; i++)
            {
                ReleaseImage(images[i]);
            }

            _swapchainHandles.Free(handle);
            throw;
        }
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
        _ = _retiring.RemoveAll(retired => retired.Frame <= _completedFrameNumber);
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

    private SwapchainState LiveSwapchain(SwapchainHandle swapchain, string parameterName)
    {
        if (swapchain.IsNull)
        {
            throw new ArgumentException("The Swapchain handle is null.", parameterName);
        }

        return _swapchainHandles.IsValid(swapchain.Value) ? _swapchains[swapchain.Value.Index]! : throw new StaleHandleException(swapchain.Value);
    }

    /// <summary>A live swapchain: its window, surface, backend id, what it is, its images and its creation order.</summary>
    private sealed record SwapchainState(
        IWindow Window, WindowSurfaceSource Source, ulong Surface, ulong Id, SwapchainInfo Info, SwapchainOptions Options, SwapchainImage[] Images, long Sequence);

    /// <summary>A destroyed swapchain still in a deletion queue, retired once <paramref name="Frame"/> has completed.</summary>
    private readonly record struct RetiringSwapchain(IWindow Window, long Frame);
}
