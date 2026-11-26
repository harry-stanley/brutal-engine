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
/// <b>Contract checks.</b> <see cref="Destroy"/> and <see cref="SetDebugName"/> take only a live id of the named kind; a
/// view's texture must resolve, through the front end's table, to a live texture; <see cref="GetFormatSupport"/> takes only
/// a defined format other than <see cref="TextureFormat.Undefined"/>; and nothing but <see cref="Dispose"/> is called after
/// disposal. The fake can't tell which frames used an object (that needs the command reader), so it doesn't check that a
/// destroyed object's frames completed.
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
/// <b>Not yet.</b> The surface and swapchain members throw <see cref="NotSupportedException"/> until #235 adds them, and the
/// frame members until #236 does.
/// </para>
/// <para><b>Threading.</b> Not thread-safe, like the contract: the owner thread only.</para>
/// </remarks>
public sealed unsafe class FakeBackendDevice : IBackendDevice
{
    // The alignment of each mapped block, in bytes.
    private const nuint MapAlignment = 64;

    private readonly FakeBackendScript _script;
    private readonly Dictionary<ulong, ObjectEntry> _live = [];
    private readonly List<FakeBackendCall> _calls = [];
    private ulong _nextId = 1;
    private long _validationErrors;
    private long _validationWarnings;
    private bool _disposed;

    internal FakeBackendDevice(FakeBackendScript script, BackendDeviceRequest request, GraphicsAdapterInfo adapter, BackendObjectTable objects)
    {
        _script = script;
        Objects = objects;
        Request = request;
        Adapter = adapter;
        Capabilities = script.Capabilities;
    }

    /// <summary>The request the device was created with.</summary>
    public BackendDeviceRequest Request { get; }

    /// <summary>The front end's handle table the device was created with, which it reads to resolve handles.</summary>
    public BackendObjectTable Objects { get; }

    /// <summary>Every call the device received that passed its checks, in order; still readable after disposal.</summary>
    public IReadOnlyList<FakeBackendCall> Calls => _calls;

    /// <summary>The number of objects created and not yet destroyed.</summary>
    public int LiveObjectCount => _live.Count;

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

        Free(_live[id]);
        _ = _live.Remove(id);
        Record(FakeBackendOperation.Destroy, kind, id);
    }

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, until #235 adds surfaces.</exception>
    public Result<ulong> CreateSurface(WindowSurfaceSource source) => throw NotYet("#235");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, until #235 adds surfaces.</exception>
    public void DestroySurface(ulong surface) => throw NotYet("#235");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, until #235 adds swapchains.</exception>
    public Result<BackendSwapchain> CreateSwapchain(in BackendSwapchainRequest request) => throw NotYet("#235");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, until #235 adds swapchains.</exception>
    public void DestroySwapchain(ulong swapchain) => throw NotYet("#235");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, until #235 adds swapchains.</exception>
    public void GetSwapchainImage(ulong swapchain, int imageIndex, out ulong texture, out ulong view) => throw NotYet("#235");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, until #236 adds frames.</exception>
    public BackendAcquire AcquireImage(ulong swapchain, int frameSlot) => throw NotYet("#236");

    /// <inheritdoc/>
    /// <exception cref="NotSupportedException">Always, until #236 adds frames.</exception>
    public BackendPresent Present(ulong swapchain, int imageIndex) => throw NotYet("#236");

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

    /// <summary>Destroys every object still alive and frees its mapped memory.</summary>
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

    private void Record(FakeBackendOperation operation, BackendObjectKind? kind, ulong id) =>
        _calls.Add(new FakeBackendCall(operation, kind, id, -1));

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw Violation("a call after Dispose");
        }
    }

    /// <summary>A live object: its kind and, for a host-visible buffer, its mapped block.</summary>
    private readonly record struct ObjectEntry(BackendObjectKind Kind, nint Mapped);
}
