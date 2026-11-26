using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using BrutalEngine.Core;
using BrutalEngine.Graphics;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.TestUtilities;

/// <summary>
/// An in-memory <see cref="IBackendDevice"/> created by <see cref="FakeGraphicsBackend"/>. It follows the backend contract
/// and throws <see cref="InvalidOperationException"/> ("contract violation: ...") when the front end breaks a rule, so
/// front-end tests catch contract bugs on any OS without a GPU.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ids.</b> Every object gets its id from one counter that starts at 1 and only grows, so ids are non-zero and never
/// reused within a device, whatever the kind.
/// </para>
/// <para>
/// <b>Contract checks.</b> <see cref="Destroy"/> and <see cref="SetDebugName"/> take only a live id of the named kind, and
/// <see cref="Destroy"/> never a swapchain image's; a view's texture must resolve, through the front end's table, to a live
/// texture; <see cref="GetFormatSupport"/> takes only a defined format other than <see cref="TextureFormat.Undefined"/>; a
/// window has at most one live surface, a surface at most one swapchain that isn't retired, and only a surface without
/// swapchains is destroyed; a swapchain is created for a window that isn't minimised, from its surface's own source; a
/// retired swapchain is only read or destroyed; image indices and frame slots are in range; and nothing but
/// <see cref="Dispose"/> is called after disposal. The fake can't tell which frames used an object (that needs the command reader), so it doesn't check that a
/// destroyed object's frames completed. Nor does it check that the front end destroys every view before its texture: both
/// checks come with #118.
/// </para>
/// <para>
/// <b>Memory.</b> Each <see cref="BackendMemory.HostUpload"/> or <see cref="BackendMemory.HostReadback"/> buffer gets a
/// zeroed unmanaged block of its size from the request's <see cref="BackendDeviceRequest.Allocator"/>, as its mapped
/// pointer, freed when the buffer is destroyed or the device disposed; wrap the allocator in a
/// <see cref="Core.Memory.TrackingAllocator"/> to see leaks.
/// </para>
/// <para>
/// <b>Recording.</b> Every call that passes its checks is appended to <see cref="Calls"/>, which allocates when the list
/// grows.
/// </para>
/// <para>
/// <b>Surfaces and swapchains.</b> With a <see cref="BackendDeviceRequest.CompatibleSurface"/>, the device makes the
/// selection surface when it is created, and the first <see cref="CreateSurface"/> for that same source hands it over. Each
/// swapchain image's texture and view get ids when the swapchain is created; <see cref="AcquireImage"/> hands out the
/// images in turn. Scripted statuses (<see cref="FakeBackendScript.SwapchainStatusNext"/>) replace the success of the next
/// acquire or present.
/// </para>
/// <para>
/// <b>Not yet.</b> The frame members throw <see cref="NotSupportedException"/> until #236 adds them, with the frame-order
/// checks of <see cref="AcquireImage"/> and <see cref="Present"/>.
/// </para>
/// <para><b>Threading.</b> Not thread-safe, like the contract: the owner thread only.</para>
/// </remarks>
public sealed unsafe class FakeBackendDevice : IBackendDevice
{
    // The alignment of each mapped block, in bytes.
    private const nuint MapAlignment = 64;

    private readonly FakeBackendScript _script;
    private readonly Dictionary<ulong, ObjectEntry> _live = [];
    private readonly Dictionary<ulong, SurfaceState> _surfaces = [];
    private readonly Dictionary<ulong, SwapchainState> _swapchains = [];
    private readonly List<FakeBackendCall> _calls = [];
    private ulong _nextId = 1;
    private long _validationErrors;
    private long _validationWarnings;
    private ulong _selectionSurface;
    private bool _disposed;

    internal FakeBackendDevice(FakeBackendScript script, BackendDeviceRequest request, GraphicsAdapterInfo adapter, BackendObjectTable objects)
    {
        _script = script;
        Objects = objects;
        Request = request;
        Adapter = adapter;
        Capabilities = script.Capabilities;
        if (request.CompatibleSurface is not null)
        {
            _selectionSurface = _nextId++;
            _surfaces.Add(_selectionSurface, new SurfaceState(request.CompatibleSurface) { HandedOver = false });
        }
    }

    /// <summary>The request the device was created with.</summary>
    public BackendDeviceRequest Request { get; }

    /// <summary>The front end's handle table the device was created with, which it reads to resolve handles.</summary>
    public BackendObjectTable Objects { get; }

    /// <summary>Every call the device received that passed its checks, in order; still readable after disposal.</summary>
    public IReadOnlyList<FakeBackendCall> Calls => _calls;

    /// <summary>
    /// The number of objects, surfaces and swapchains created and not yet destroyed: everything the front end must destroy.
    /// Swapchain images and a selection surface not yet handed over don't count.
    /// </summary>
    public int LiveObjectCount
    {
        get
        {
            int count = _live.Count + _swapchains.Count;
            foreach (SwapchainState swapchain in _swapchains.Values)
            {
                count -= 2 * swapchain.ImageCount;
            }

            foreach (SurfaceState surface in _surfaces.Values)
            {
                count += surface.HandedOver ? 1 : 0;
            }

            return count;
        }
    }

    /// <summary>Whether <see cref="Dispose"/> has run.</summary>
    public bool IsDisposed => _disposed;

    /// <inheritdoc/>
    public GraphicsAdapterInfo Adapter { get; }

    /// <inheritdoc/>
    /// <remarks>The script's <see cref="FakeBackendScript.Capabilities"/> when the device was created.</remarks>
    public GraphicsCapabilities Capabilities { get; }

    /// <inheritdoc/>
    /// <remarks>Grows only by <see cref="AddValidationMessages"/> until #236 makes <see cref="Execute"/> report an injected error.</remarks>
    public long ValidationErrors => _validationErrors;

    /// <inheritdoc/>
    /// <remarks>Grows only by <see cref="AddValidationMessages"/>.</remarks>
    public long ValidationWarnings => _validationWarnings;

    /// <summary>Makes the "validation layer" report messages, as if the device's work had broken a usage rule.</summary>
    /// <param name="errors">The number of error messages to add.</param>
    /// <param name="warnings">The number of warning messages to add.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count is negative.</exception>
    /// <exception cref="InvalidOperationException">The device was created without validation.</exception>
    public void AddValidationMessages(int errors, int warnings)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(errors);
        ArgumentOutOfRangeException.ThrowIfNegative(warnings);
        if (!Request.Validation)
        {
            throw new InvalidOperationException("The device was created without validation, so it reports no messages.");
        }

        _validationErrors += errors;
        _validationWarnings += warnings;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Contract violation: an undefined format, or a call after disposal.</exception>
    /// <remarks>Returns the script's <see cref="FakeBackendScript.FormatSupport"/> for the format.</remarks>
    public FormatSupport GetFormatSupport(TextureFormat format)
    {
        ThrowIfDisposed();
        // A range check, not Enum.IsDefined, which allocates after a GC; a test pins the range to the enum's members.
        if (format <= TextureFormat.Undefined || format > TextureFormat.Depth32Float)
        {
            throw Violation($"GetFormatSupport of {format}, not a defined format");
        }

        Record(FakeBackendOperation.GetFormatSupport, null, 0);
        return _script.FormatSupport(format);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// Contract violation: a size below 1, a memory value that isn't a <see cref="BackendMemory"/> member, or a call after
    /// disposal.
    /// </exception>
    public Result<BackendBuffer> CreateBuffer(in BufferDescription description, BackendMemory memory)
    {
        ThrowIfDisposed();
        if (description.Size < 1)
        {
            throw Violation($"CreateBuffer of {description.Size} bytes");
        }

        if ((uint)memory > (uint)BackendMemory.HostReadback)
        {
            throw Violation($"CreateBuffer in {memory}, not a BackendMemory member");
        }

        if (_script.TakeFailure(FakeBackendOperation.CreateBuffer, out EngineError error))
        {
            Record(FakeBackendOperation.CreateBuffer, BackendObjectKind.Buffer, 0);
            return error;
        }

        nint mapped = 0;
        if (memory is BackendMemory.HostUpload or BackendMemory.HostReadback)
        {
            nuint size = (nuint)description.Size;
            void* block = Request.Allocator.Allocate(size, MapAlignment);
            // Sound: the allocator returned a live block of exactly size bytes, which nothing else references yet.
            NativeMemory.Clear(block, size);
            mapped = (nint)block;
        }

        ulong id = Add(FakeBackendOperation.CreateBuffer, BackendObjectKind.Buffer, mapped);
        return new BackendBuffer(id, mapped);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Contract violation: a call after disposal.</exception>
    public Result<ulong> CreateTexture(in TextureDescription description) =>
        CreateObject(FakeBackendOperation.CreateTexture, BackendObjectKind.Texture);

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// Contract violation: the description's texture doesn't resolve to a live texture of this device, or a call after
    /// disposal.
    /// </exception>
    public Result<ulong> CreateTextureView(in TextureViewDescription description)
    {
        ThrowIfDisposed();
        ulong texture = Objects.GetId(description.Texture);
        if (!IsLive(BackendObjectKind.Texture, texture))
        {
            throw Violation($"CreateTextureView of texture id {texture}, not a live texture");
        }

        return CreateObject(FakeBackendOperation.CreateTextureView, BackendObjectKind.TextureView);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Contract violation: a call after disposal.</exception>
    public Result<ulong> CreateSampler(in SamplerDescription description) =>
        CreateObject(FakeBackendOperation.CreateSampler, BackendObjectKind.Sampler);

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Contract violation: an id that isn't a live object of that kind, or a call after disposal.</exception>
    public void SetDebugName(BackendObjectKind kind, ulong id, ReadOnlySpan<byte> utf8Name)
    {
        ThrowIfDisposed();
        if (!IsLive(kind, id))
        {
            throw Violation($"SetDebugName of {kind} id {id}, not a live {kind}");
        }

        Record(FakeBackendOperation.SetDebugName, kind, id);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// Contract violation: an id that isn't a live object of that kind (unknown, already destroyed, or another kind), or a
    /// call after disposal.
    /// </exception>
    public void Destroy(BackendObjectKind kind, ulong id)
    {
        ThrowIfDisposed();
        if (!IsLive(kind, id))
        {
            throw Violation($"Destroy of {kind} id {id}, not a live {kind}");
        }

        if (_live[id].Swapchain != 0)
        {
            throw Violation($"Destroy of {kind} id {id}, an image of swapchain {_live[id].Swapchain}");
        }

        Free(_live[id]);
        _ = _live.Remove(id);
        Record(FakeBackendOperation.Destroy, kind, id);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// Contract violation: the window already has a live surface, or a call after disposal.
    /// </exception>
    /// <remarks>Takes a pending <see cref="FakeBackendOperation.CreateSurface"/> failure after the checks.</remarks>
    public Result<ulong> CreateSurface(WindowSurfaceSource source)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(source);
        foreach (SurfaceState surface in _surfaces.Values)
        {
            if (surface.HandedOver && ReferenceEquals(surface.Source, source))
            {
                throw Violation("CreateSurface for a window that already has a live surface");
            }
        }

        if (_script.TakeFailure(FakeBackendOperation.CreateSurface, out EngineError error))
        {
            Record(FakeBackendOperation.CreateSurface, null, 0);
            return error;
        }

        ulong id;
        if (_selectionSurface != 0 && ReferenceEquals(source, Request.CompatibleSurface))
        {
            id = _selectionSurface;
            _selectionSurface = 0;
            _surfaces[id].HandedOver = true;
        }
        else
        {
            id = _nextId++;
            _surfaces.Add(id, new SurfaceState(source) { HandedOver = true });
        }

        Record(FakeBackendOperation.CreateSurface, null, id);
        return id;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// Contract violation: an id that isn't a live surface the front end received, a surface with a swapchain (retired or
    /// not), or a call after disposal.
    /// </exception>
    public void DestroySurface(ulong surface)
    {
        ThrowIfDisposed();
        _ = LiveSurface(surface, "DestroySurface");
        foreach (SwapchainState swapchain in _swapchains.Values)
        {
            if (swapchain.Surface == surface)
            {
                throw Violation($"DestroySurface of surface {surface}, which still has a swapchain");
            }
        }

        _ = _surfaces.Remove(surface);
        Record(FakeBackendOperation.DestroySurface, null, surface);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// Contract violation: the surface isn't live, the request's source isn't the surface's, the options are
    /// <see langword="null"/>, the window is minimised, the old swapchain isn't a live, unretired swapchain of that surface,
    /// the surface already has an unretired swapchain and none is replaced, or a call after disposal. Also thrown, with no
    /// effect, when the script's <see cref="FakeBackendScript.SwapchainInfo"/> reports no images.
    /// </exception>
    /// <remarks>
    /// The checks run first, the script's swapchain info among them; then the old swapchain is retired, then a pending
    /// <see cref="FakeBackendOperation.CreateSwapchain"/> failure is taken, so a failed recreation still retires it.
    /// </remarks>
    public Result<BackendSwapchain> CreateSwapchain(in BackendSwapchainRequest request)
    {
        ThrowIfDisposed();
        SurfaceState surface = LiveSurface(request.Surface, "CreateSwapchain");
        if (!ReferenceEquals(request.Source, surface.Source) || request.Options is null)
        {
            throw Violation("CreateSwapchain whose source isn't its surface's window, or with no options");
        }

        if (request.Source.IsMinimised)
        {
            throw Violation("CreateSwapchain for a minimised window");
        }

        SwapchainState? old = null;
        if (request.OldSwapchain != 0)
        {
            old = LiveSwapchain(request.OldSwapchain, "CreateSwapchain (the old swapchain)", unretired: true);
            if (old.Surface != request.Surface)
            {
                throw Violation($"CreateSwapchain replacing swapchain {request.OldSwapchain} of another surface");
            }
        }
        else
        {
            foreach (SwapchainState swapchain in _swapchains.Values)
            {
                if (swapchain.Surface == request.Surface && !swapchain.Retired)
                {
                    throw Violation($"CreateSwapchain on surface {request.Surface}, which has a swapchain it doesn't replace");
                }
            }
        }

        SwapchainInfo info = _script.SwapchainInfo(request);
        if (info.ImageCount < 1)
        {
            throw new InvalidOperationException($"The script's swapchain info has {info.ImageCount} images; at least 1 is needed.");
        }

        old?.Retired = true;
        if (_script.TakeFailure(FakeBackendOperation.CreateSwapchain, out EngineError error))
        {
            Record(FakeBackendOperation.CreateSwapchain, null, 0);
            return error;
        }

        ulong id = _nextId++;
        _swapchains.Add(id, new SwapchainState(request.Surface, info.ImageCount));
        for (int i = 0; i < info.ImageCount; i++)
        {
            // Texture then view, per image: GetSwapchainImage computes the ids from this order.
            _live.Add(_nextId++, new ObjectEntry(BackendObjectKind.Texture, 0, id));
            _live.Add(_nextId++, new ObjectEntry(BackendObjectKind.TextureView, 0, id));
        }

        Record(FakeBackendOperation.CreateSwapchain, null, id);
        return new BackendSwapchain(id, info);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Contract violation: an id that isn't a live swapchain, or a call after disposal.</exception>
    public void DestroySwapchain(ulong swapchain)
    {
        ThrowIfDisposed();
        SwapchainState state = LiveSwapchain(swapchain, "DestroySwapchain", unretired: false);
        for (ulong i = 1; i <= 2 * (ulong)state.ImageCount; i++)
        {
            _ = _live.Remove(swapchain + i);
        }

        _ = _swapchains.Remove(swapchain);
        Record(FakeBackendOperation.DestroySwapchain, null, swapchain);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// Contract violation: an id that isn't a live swapchain, an image index out of range, or a call after disposal.
    /// </exception>
    public void GetSwapchainImage(ulong swapchain, int imageIndex, out ulong texture, out ulong view)
    {
        ThrowIfDisposed();
        CheckImage(LiveSwapchain(swapchain, "GetSwapchainImage", unretired: false), imageIndex, "GetSwapchainImage");
        texture = swapchain + 1 + (2 * (ulong)imageIndex);
        view = texture + 1;
        Record(FakeBackendOperation.GetSwapchainImage, null, swapchain);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// Contract violation: an id that isn't a live, unretired swapchain, a frame slot outside the request's frames in flight,
    /// or a call after disposal.
    /// </exception>
    /// <remarks>
    /// Takes a pending status (<see cref="FakeBackendScript.SwapchainStatusNext"/>); otherwise succeeds. Each acquired image is
    /// the one after the swapchain's previous one. Allocates nothing.
    /// </remarks>
    public BackendAcquire AcquireImage(ulong swapchain, int frameSlot)
    {
        ThrowIfDisposed();
        SwapchainState state = LiveSwapchain(swapchain, "AcquireImage", unretired: true);
        if ((uint)frameSlot >= (uint)Request.FramesInFlight)
        {
            throw Violation($"AcquireImage for frame slot {frameSlot}, outside the {Request.FramesInFlight} frames in flight");
        }

        Record(FakeBackendOperation.AcquireImage, null, swapchain, frameSlot);
        _ = _script.TakeStatus(FakeBackendOperation.AcquireImage, out SwapchainStatus status, out int nativeCode);
        if (status is not (SwapchainStatus.Success or SwapchainStatus.Suboptimal))
        {
            return new BackendAcquire(status, -1, nativeCode);
        }

        int image = state.NextImage;
        state.NextImage = (image + 1) % state.ImageCount;
        return new BackendAcquire(status, image, nativeCode);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">
    /// Contract violation: an id that isn't a live, unretired swapchain, an image index out of range, or a call after
    /// disposal.
    /// </exception>
    /// <remarks>Takes a pending status (<see cref="FakeBackendScript.SwapchainStatusNext"/>); otherwise succeeds. Allocates nothing.</remarks>
    public BackendPresent Present(ulong swapchain, int imageIndex)
    {
        ThrowIfDisposed();
        CheckImage(LiveSwapchain(swapchain, "Present", unretired: true), imageIndex, "Present");
        Record(FakeBackendOperation.Present, null, swapchain);
        _ = _script.TakeStatus(FakeBackendOperation.Present, out SwapchainStatus status, out int nativeCode);
        return new BackendPresent(status, nativeCode);
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, until #236 adds frames.</exception>
    public Result WaitForFrame(int frameSlot) => throw NotYet("#236");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, until #236 adds frames.</exception>
    public Result<bool> IsFrameComplete(int frameSlot) => throw NotYet("#236");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, until #236 adds frames.</exception>
    public Result BeginFrame(int frameSlot) => throw NotYet("#236");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, until #236 adds frames.</exception>
    public Result Execute(in FramePlan plan) => throw NotYet("#236");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, until #236 adds frames.</exception>
    public bool TryReadTimestamps(int frameSlot, Span<ulong> ticks) => throw NotYet("#236");

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Contract violation: a call after disposal.</exception>
    /// <remarks>Takes a pending <see cref="FakeBackendOperation.WaitIdle"/> failure; otherwise succeeds at once.</remarks>
    public Result WaitIdle()
    {
        ThrowIfDisposed();
        Record(FakeBackendOperation.WaitIdle, null, 0);
        return _script.TakeFailure(FakeBackendOperation.WaitIdle, out EngineError error) ? error : Result.Success;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Checks the device and records the call; the next <see cref="Execute"/> reports the error in
    /// <see cref="ValidationErrors"/> once #236 adds frames.
    /// </remarks>
    public void InjectValidationError()
    {
        ThrowIfDisposed();
        if (!Request.Validation)
        {
            throw new InvalidOperationException(
                "InjectValidationError needs a device created with validation: without a layer to catch it, the violation would prove nothing.");
        }

        Record(FakeBackendOperation.InjectValidationError, null, 0);
    }

    /// <summary>Destroys every object, surface and swapchain still alive, the selection surface included, and frees mapped memory.</summary>
    /// <remarks>Idempotent: only the first call does anything or is recorded. <see cref="Calls"/> stays readable.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (ObjectEntry entry in _live.Values)
        {
            Free(entry);
        }

        _live.Clear();
        _swapchains.Clear();
        _surfaces.Clear();
        _selectionSurface = 0;
        Record(FakeBackendOperation.Dispose, null, 0);
    }

    private static InvalidOperationException Violation(string rule) => new($"contract violation: {rule}.");

    private static NotSupportedException NotYet(string issue) =>
        new($"The fake backend device doesn't implement this member yet ({issue} adds it).");

    private Result<ulong> CreateObject(FakeBackendOperation operation, BackendObjectKind kind)
    {
        ThrowIfDisposed();
        if (_script.TakeFailure(operation, out EngineError error))
        {
            Record(operation, kind, 0);
            return error;
        }

        return Add(operation, kind, 0);
    }

    private ulong Add(FakeBackendOperation operation, BackendObjectKind kind, nint mapped)
    {
        ulong id = _nextId++;
        _live.Add(id, new ObjectEntry(kind, mapped));
        Record(operation, kind, id);
        return id;
    }

    private bool IsLive(BackendObjectKind kind, ulong id) => _live.TryGetValue(id, out ObjectEntry entry) && entry.Kind == kind;

    private void Free(ObjectEntry entry)
    {
        if (entry.Mapped != 0)
        {
            Request.Allocator.Free((void*)entry.Mapped);
        }
    }

    private SurfaceState LiveSurface(ulong id, string member)
    {
        if (!_surfaces.TryGetValue(id, out SurfaceState? surface) || !surface.HandedOver)
        {
            throw Violation($"{member} of id {id}, not a live surface");
        }

        return surface;
    }

    private SwapchainState LiveSwapchain(ulong id, string member, bool unretired)
    {
        if (!_swapchains.TryGetValue(id, out SwapchainState? swapchain) || (unretired && swapchain.Retired))
        {
            throw Violation($"{member} of id {id}, not a live{(unretired ? ", unretired" : "")} swapchain");
        }

        return swapchain;
    }

    private static void CheckImage(SwapchainState swapchain, int imageIndex, string member)
    {
        if ((uint)imageIndex >= (uint)swapchain.ImageCount)
        {
            throw Violation($"{member} of image {imageIndex}, outside the swapchain's {swapchain.ImageCount} images");
        }
    }

    private void Record(FakeBackendOperation operation, BackendObjectKind? kind, ulong id, int frameSlot = -1) =>
        _calls.Add(new FakeBackendCall(operation, kind, id, frameSlot));

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw Violation("a call after Dispose");
        }
    }

    /// <summary>
    /// A live object: its kind, its mapped block for a host-visible buffer, and the swapchain that owns it for a swapchain
    /// image's texture or view (0 otherwise).
    /// </summary>
    private readonly record struct ObjectEntry(BackendObjectKind Kind, nint Mapped, ulong Swapchain = 0);

    /// <summary>A surface: its window's source, and whether the front end has received its id.</summary>
    private sealed class SurfaceState(WindowSurfaceSource source)
    {
        public WindowSurfaceSource Source { get; } = source;

        public bool HandedOver { get; set; }
    }

    /// <summary>A swapchain: its surface, its image count (image ids follow its own id), and its acquire state.</summary>
    private sealed class SwapchainState(ulong surface, int imageCount)
    {
        public ulong Surface { get; } = surface;

        public int ImageCount { get; } = imageCount;

        public bool Retired { get; set; }

        public int NextImage { get; set; }
    }
}
