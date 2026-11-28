using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Internal;

namespace BrutalEngine.Graphics;

/// <summary>
/// The render interface's device: it owns every GPU object as a typed handle, validates every call, and drives one
/// backend device (docs/design/graphics.md sections 1, 2, 13 and 14).
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> Created by <see cref="Create"/> and owned by its caller (the Runtime host, a test, a sample), which
/// disposes it. The device owns its backend device and disposes it in <see cref="Dispose"/>. It never owns or disposes
/// the <see cref="IGraphicsBackend"/>, the logger, the allocator or the compatible window: they must outlive it.
/// </para>
/// <para>
/// <b>Threading.</b> The thread that calls <see cref="Create"/> owns the device. Every member except
/// <see cref="Adapter"/> and <see cref="Capabilities"/> checks it first and throws <see cref="WrongThreadException"/>,
/// naming the member, from any other thread. <see cref="Adapter"/> and <see cref="Capabilities"/> are immutable and
/// read on any thread.
/// </para>
/// <para>
/// <b>Errors.</b> Misuse throws: invalid options or arguments, a wrong thread, a call after <see cref="Dispose"/>.
/// Expected failures return a failed <see cref="Result{T}"/>: no backend runtime, no suitable adapter, a missing
/// required feature.
/// </para>
/// <para>
/// <b>Allocation.</b> <see cref="Create"/>, <see cref="EnumerateAdapters"/>, object creation, a new label,
/// <see cref="ThrowIfLeaks"/> with leaks and <see cref="Dispose"/> allocate. <see cref="Adapter"/>,
/// <see cref="Capabilities"/>, <see cref="GetFormatSupport"/>, every <c>Destroy</c>, <see cref="GetDefaultView"/>,
/// <see cref="CreateDebugLabel"/> for a name already interned, <see cref="GetDebugLabelName"/>,
/// <see cref="Statistics"/>, <see cref="BeginFrame"/>, <see cref="SubmitFrame"/>, <see cref="FrameNumber"/>,
/// <see cref="CompletedFrameNumber"/> and <see cref="IsLost"/> allocate nothing, except an exception they throw; so do
/// <see cref="WriteBuffer"/> and <see cref="WriteTexture"/> once their staging block exists.
/// <see cref="WaitIdle"/> may allocate. Unmanaged memory (frame slots, deletion queues, upload copies) comes from
/// <see cref="GraphicsDeviceOptions.Allocator"/> and is freed by <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed partial class GraphicsDevice : IDisposable
{
    private const string LogCategoryName = "Graphics";

    private readonly IGraphicsBackend _factory;
    private readonly BackendDeviceRequest _request;
    private readonly GraphicsDeviceOptions _options;
    private readonly BackendObjectTable _objects;
    private IBackendDevice _backend;
    private readonly HandleTables _handles;
    private readonly HandleAllocator _labels = new();
    private readonly Dictionary<string, DebugLabel> _labelsByName = new(StringComparer.Ordinal);
    private readonly LogCategory _log;
    private readonly ThreadAffinity _owner;
    private string[] _labelNames = [];
    private bool _disposed;

    private GraphicsDevice(IGraphicsBackend factory, BackendDeviceRequest request, IBackendDevice backend, BackendObjectTable objects, LogCategory log, GraphicsDeviceOptions options)
    {
        (_factory, _request, _options) = (factory, request, options);
        _compatibleWindow = options.CompatibleWindow;
        // The request's surface source is kept: the backend hands the selection surface over for that instance only.
        _compatibleSurface = request.CompatibleSurface;
        _slots = CreateSlots(options.FramesInFlight, options.Allocator);
        _allocator = options.Allocator;
        _planner = new FramePlanner(options.Allocator);
        _ringBytes = options.UploadRingBytesPerFrame;
        _backend = backend;
        _objects = objects;
        _handles = new HandleTables(objects);
        _log = log;
        _owner = ThreadAffinity.CurrentThread();
        Adapter = backend.Adapter;
        Capabilities = backend.Capabilities;
    }

    /// <summary>The adapter the device runs on, as the backend reported it.</summary>
    /// <remarks>Immutable after <see cref="Create"/>. Any thread, also after <see cref="Dispose"/>. Allocates nothing.</remarks>
    public GraphicsAdapterInfo Adapter { get; }

    /// <summary>
    /// The device's enabled features (every required one, plus the optional ones the adapter has) and the adapter's limits.
    /// </summary>
    /// <remarks>Immutable after <see cref="Create"/>. Any thread, also after <see cref="Dispose"/>. Allocates nothing.</remarks>
    public GraphicsCapabilities Capabilities { get; }

    /// <summary>Creates a graphics device on the adapter <see cref="GraphicsDeviceOptions.Adapter"/> selects.</summary>
    /// <param name="backend">The backend to create the device with. Not owned: never disposed, whatever the outcome.</param>
    /// <param name="options">The device's settings, checked first.</param>
    /// <returns>
    /// The device, owned by the caller. Fails with <see cref="ErrorKind.Unsupported"/> when the compatible window was
    /// created with <c>WindowSurface.None</c>, when no adapter is suitable (the message lists every adapter with its
    /// rejection reasons), when an adapter override matches no suitable adapter (the message names the override; it never
    /// falls back to another adapter), or when the backend device lacks a required feature. A failure of the backend's
    /// enumeration or device creation is returned unchanged, for example <see cref="ErrorKind.Unavailable"/> without a
    /// backend runtime.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="backend"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An option breaks its rule (see <see cref="GraphicsDeviceOptions"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its range (see <see cref="GraphicsDeviceOptions"/>).</exception>
    /// <exception cref="ObjectDisposedException">The compatible window has been disposed.</exception>
    /// <exception cref="WrongThreadException">A compatible window is set and this isn't its thread.</exception>
    /// <exception cref="OutOfMemoryException">
    /// The allocator couldn't provide the frame slots' memory; the backend device is disposed and nothing stays allocated.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Only suitable adapters (no rejection reasons) are candidates. An override narrows them first: the adapter at the
    /// enumeration index, the names containing the substring (ordinal, ignoring case), or the adapters of the type;
    /// <see cref="AdapterSelection.Auto"/> keeps them all. The rest are ranked <see cref="AdapterType.Discrete"/>, then
    /// <see cref="AdapterType.Integrated"/>, <see cref="AdapterType.Virtual"/>, <see cref="AdapterType.Cpu"/> and
    /// <see cref="AdapterType.Other"/>, ties going to the lowest <see cref="GraphicsAdapterInfo.Index"/>, and the first is
    /// chosen. Each rejected adapter is logged at
    /// <see cref="LogLevel.Information"/> under <c>"Graphics"</c> with its reasons, whether or not creation succeeds.
    /// </para>
    /// <para>
    /// Options are checked before the backend is called. On failure nothing is left alive: a backend device without a
    /// required feature is disposed before the failure returns. The calling thread becomes the device's owner thread.
    /// Allocates.
    /// </para>
    /// </remarks>
    public static Result<GraphicsDevice> Create(IGraphicsBackend backend, GraphicsDeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (!TryBuildRequest(options, out BackendDeviceRequest request, out EngineError error))
        {
            return error;
        }

        LogCategory log = options.Logger.CreateCategory(LogCategoryName);
        var objects = new BackendObjectTable();
        Result<IBackendDevice> opened = OpenBackendDevice(backend, request, options, log, objects);
        if (!opened.TryGetValue(out IBackendDevice? device))
        {
            return opened.Error;
        }

        try
        {
            return new GraphicsDevice(backend, request, device, objects, log, options);
        }
        catch
        {
            // The frame slots' memory couldn't be allocated: nothing else holds the backend device.
            device.Dispose();
            throw;
        }
    }

    /// <summary>Lists every adapter the backend can see for these options, each with the reasons it can't serve them.</summary>
    /// <param name="backend">The backend to ask. Not owned: never disposed.</param>
    /// <param name="options">The settings the adapters are judged against, checked first; the adapter selection is ignored.</param>
    /// <returns>
    /// The adapters in the backend's order, each with empty <see cref="GraphicsAdapterInfo.RejectionReasons"/> when it is
    /// suitable; a list without a suitable adapter, or an empty one, is a success. Fails with
    /// <see cref="ErrorKind.Unsupported"/> for a compatible window created with <c>WindowSurface.None</c>, and with the
    /// backend's failure unchanged, for example <see cref="ErrorKind.Unavailable"/> without a backend runtime.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="backend"/> or <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">An option breaks its rule (see <see cref="GraphicsDeviceOptions"/>).</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its range (see <see cref="GraphicsDeviceOptions"/>).</exception>
    /// <exception cref="ObjectDisposedException">The compatible window has been disposed.</exception>
    /// <exception cref="WrongThreadException">A compatible window is set and this isn't its thread.</exception>
    /// <remarks>
    /// With a <see cref="GraphicsDeviceOptions.CompatibleWindow"/> the backend creates a temporary surface for that window
    /// to check presentation, and a window has at most one live surface: don't call it while any device presents to that
    /// window. Nothing checks this, because devices don't know each other. Nothing outlives the call; no device is
    /// created. Allocates.
    /// </remarks>
    public static Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(IGraphicsBackend backend, GraphicsDeviceOptions options)
    {
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return TryBuildRequest(options, out BackendDeviceRequest request, out EngineError error)
            ? backend.EnumerateAdapters(request)
            : new Result<IReadOnlyList<GraphicsAdapterInfo>>(error);
    }

    /// <summary>Reports what the device can do with textures of one format.</summary>
    /// <param name="format">A defined format other than <see cref="TextureFormat.Undefined"/>.</param>
    /// <returns>The backend's answer; <see cref="FormatSupport.None"/> when the format can't be used at all.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="format"/> is <see cref="TextureFormat.Undefined"/> or not a <see cref="TextureFormat"/> member.</exception>
    /// <exception cref="InvalidOperationException">
    /// The device has no backend device to ask: a <see cref="Recover"/> failed before making one.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Constant for the device's lifetime. Owner thread. Allocates nothing, except the exception.</remarks>
    public FormatSupport GetFormatSupport(TextureFormat format)
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(GetFormatSupport));
        // A range check, not Enum.IsDefined, which allocates after a GC.
        if (format is <= TextureFormat.Undefined or > TextureFormat.Depth32Float)
        {
            throw new ArgumentOutOfRangeException(nameof(format), format, "GetFormatSupport: not a defined TextureFormat other than Undefined.");
        }

        return _backendGone
            ? throw new InvalidOperationException("GetFormatSupport: the device has no backend device (a recovery failed).")
            : _backend.GetFormatSupport(format);
    }

    /// <summary>A snapshot of the device's counters.</summary>
    /// <value>
    /// The frames submitted (<see cref="FrameNumber"/>), the bytes the last submitted frame uploaded and the bytes ever
    /// uploaded outside the ring (<see cref="WriteBuffer"/>), the live objects over every kind (default texture views and
    /// swapchains included; swapchain images and transient allocations' buffers never count) and the backend's
    /// validation counts, those of the backend devices <see cref="Recover"/> replaced included.
    /// </value>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Read from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public GraphicsStatistics Statistics
    {
        get
        {
            ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(Statistics));
            return new GraphicsStatistics(_frameNumber, _uploadBytesLastFrame, _uploadOverflowBytes, LiveObjectCount, ValidationErrorCount, ValidationWarningCount);
        }
    }

    /// <summary>Interns a debug label: a name for passes and debug groups that the frame loop passes without strings.</summary>
    /// <param name="name">The label's name, compared ordinally.</param>
    /// <returns>
    /// The label; the same one for every call with an equal name. It stays valid for the device's lifetime: labels are never
    /// removed.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty: <c>default(DebugLabel)</c> already means no label.</exception>
    /// <exception cref="OutOfMemoryException">A table couldn't grow; no label was created.</exception>
    /// <exception cref="InvalidOperationException">
    /// Every label slot is in use (the label handle allocator is exhausted; labels are never removed); no label was
    /// created.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// The first call for a name allocates (the name's UTF-8 copy the backend reads, and the lookup entries); later calls
    /// for the same name allocate nothing. Owner thread.
    /// </remarks>
    public DebugLabel CreateDebugLabel(string name)
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(CreateDebugLabel));
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (_labelsByName.TryGetValue(name, out DebugLabel existing))
        {
            return existing;
        }

        Handle handle = _labels.Allocate();
        var label = new DebugLabel(handle);
        try
        {
            if (handle.Index >= (uint)_labelNames.Length)
            {
                Array.Resize(ref _labelNames, Math.Max(_labelNames.Length * 2, (int)handle.Index + 1));
            }

            _labelsByName.Add(name, label);
            try
            {
                _objects.SetLabel(label, name);
            }
            catch
            {
                _ = _labelsByName.Remove(name);
                throw;
            }
        }
        catch
        {
            // Out of memory part-way: nothing refers to the label, so its slot is freed and a later call starts afresh.
            _labels.Free(handle);
            throw;
        }

        _labelNames[handle.Index] = name;
        return label;
    }

    /// <summary>Returns the name a debug label was interned with.</summary>
    /// <param name="label">A label this device interned, or <c>default</c> for no label.</param>
    /// <returns>The interned string itself, not a copy; empty for <c>default</c>.</returns>
    /// <exception cref="StaleHandleException">
    /// <paramref name="label"/> isn't null and isn't a live label of this device. A label of another device is detected
    /// only when its index or generation doesn't match a label here; one that matches can't be told apart from it, and
    /// this device's name for that label is returned.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public string GetDebugLabelName(DebugLabel label)
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(GetDebugLabelName));
        if (label.IsNull)
        {
            return string.Empty;
        }

        return _labels.IsValid(label.Value) ? _labelNames[label.Value.Index] : throw new StaleHandleException(label.Value);
    }

    /// <summary>Throws if any GPU object is still alive: tests call it before <see cref="Dispose"/>.</summary>
    /// <exception cref="GpuResourceLeakException">
    /// Objects are alive; <see cref="GpuResourceLeakException.LeakedObjects"/> lists each, oldest first, as
    /// <c>Kind 'DebugName'</c>. Nothing is destroyed.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// Default texture views and swapchains (<c>Swapchain 'DebugName'</c>) count as objects; swapchain images never do.
    /// Owner thread. Allocates nothing when nothing leaks.
    /// </remarks>
    public void ThrowIfLeaks()
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(ThrowIfLeaks));
        if (LiveObjectCount == 0)
        {
            return;
        }

        throw new GpuResourceLeakException(LeakLines());
    }

    /// <summary>
    /// Waits for the GPU, destroys the objects deletion queues still hold and the upload staging buffers, logs every
    /// object still alive as a leak, destroys the live swapchains (each before its surface) and then the other objects,
    /// newest first, then disposes the backend device and frees the frame slots, once; later calls do nothing.
    /// </summary>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread, also when already disposed.</exception>
    /// <remarks>
    /// The wait happens only when a frame was submitted and the device isn't lost; if it fails, that is logged at
    /// <see cref="LogLevel.Error"/> and the objects are destroyed anyway. A frame may be open: it is never submitted.
    /// After a <see cref="Recover"/> that failed before making a backend device, nothing waits or is destroyed and the
    /// backend isn't called: only the device's own memory is freed.
    /// Each leaked object is one <see cref="LogLevel.Error"/> line under <c>"Graphics"</c>, oldest first, with its kind and
    /// debug name. Afterwards every member except <see cref="Dispose"/>, <see cref="Adapter"/> and <see cref="Capabilities"/>
    /// throws <see cref="ObjectDisposedException"/>. The backend, logger, allocator and compatible window are left alone.
    /// Allocates.
    /// </remarks>
    public void Dispose()
    {
        _owner.ThrowIfNotCurrentThread(nameof(GraphicsDevice) + "." + nameof(Dispose));
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_backendGone)
        {
            DisposeSlots(_slots);
            _planner.Dispose();
            _deferred?.Dispose();
            return;
        }

        if (_frameNumber > 0 && !IsLostCore && _backend.WaitIdle().TryGetError(out EngineError idle))
        {
            // Destroying is still valid on a hung or lost device; the objects go anyway.
            _log.Error($"Dispose: waiting for the GPU failed ({idle.Kind}, native code {idle.NativeCode}: {idle.Message}); destroying every object anyway.");
        }

        foreach (FrameSlot slot in _slots)
        {
            slot.Deletions.Retire(_backend);
        }

        DestroyUploads();

        foreach (string leaked in LeakLines())
        {
            _log.Error($"Leak: {leaked} was still alive when the device was disposed.");
        }

        DestroySwapchains();
        foreach (HandleTables.LiveObject live in _handles.TakeAll())
        {
            _backend.Destroy(live.Kind, live.Id);
        }

        _backend.Dispose();
        DisposeSlots(_slots);
        _planner.Dispose();
        _deferred?.Dispose();
    }

    // Enumerates the adapters, logs the rejected ones, selects one and creates its backend device, which must have every
    // required feature; on a failure nothing is left alive. Create and Recover share it.
    private static Result<IBackendDevice> OpenBackendDevice(IGraphicsBackend backend, BackendDeviceRequest request, GraphicsDeviceOptions options, LogCategory log, BackendObjectTable objects)
    {
        Result<IReadOnlyList<GraphicsAdapterInfo>> adapters = backend.EnumerateAdapters(request);
        if (!adapters.TryGetValue(out IReadOnlyList<GraphicsAdapterInfo>? list))
        {
            return adapters.Error;
        }

        LogRejections(log, list);
        if (!TrySelect(list, options.Adapter, out int adapterIndex, out EngineError error))
        {
            return error;
        }

        Result<IBackendDevice> created = backend.CreateDevice(request, adapterIndex, objects);
        if (!created.TryGetValue(out IBackendDevice? device))
        {
            return created.Error;
        }

        GraphicsFeatures missing = options.RequiredFeatures & ~device.Capabilities.Features;
        if (missing != GraphicsFeatures.None)
        {
            device.Dispose();
            return new EngineError(ErrorKind.Unsupported, string.Create(
                CultureInfo.InvariantCulture,
                $"Create: the device on adapter {adapterIndex} '{device.Adapter.Name}' lacks the required features {missing}."));
        }

        return new Result<IBackendDevice>(device);
    }

    private static bool TryBuildRequest(GraphicsDeviceOptions options, out BackendDeviceRequest request, out EngineError error)
    {
        WindowSurfaceSource? surface = null;
        if (options.CompatibleWindow is { } window)
        {
            if (window.VulkanSurface is not { } source)
            {
                request = null!;
                error = new EngineError(ErrorKind.Unsupported, "Create: the compatible window was created with WindowSurface.None.");
                return false;
            }

            surface = new WindowSurfaceSource(window, source);
        }

        request = new BackendDeviceRequest
        {
            Logger = options.Logger,
            Allocator = options.Allocator,
            CompatibleSurface = surface,
            Validation = options.Validation,
            FramesInFlight = options.FramesInFlight,
            RequiredFeatures = options.RequiredFeatures,
            OptionalFeatures = options.OptionalFeatures,
            GpuTimeout = options.GpuTimeout,
            ApplicationName = options.ApplicationName,
        };
        error = default;
        return true;
    }

    private static bool TrySelect(IReadOnlyList<GraphicsAdapterInfo> adapters, AdapterSelection selection, out int index, out EngineError error)
    {
        GraphicsAdapterInfo? best = null;
        foreach (GraphicsAdapterInfo adapter in adapters)
        {
            // Strictly better only, so a tie keeps the lowest index.
            if (adapter.RejectionReasons.Count == 0 && Matches(adapter, selection)
                && (best is null || Preference(adapter.Type) > Preference(best.Type)
                    || (Preference(adapter.Type) == Preference(best.Type) && adapter.Index < best.Index)))
            {
                best = adapter;
            }
        }

        if (best is not null)
        {
            index = best.Index;
            error = default;
            return true;
        }

        var message = new StringBuilder(selection.Kind == AdapterSelectionKind.Auto
            ? "Create: no suitable adapter."
            : string.Create(CultureInfo.InvariantCulture, $"Create: the adapter override '{selection}' matches no suitable adapter."));
        if (adapters.Count == 0)
        {
            _ = message.Append(" The backend found no adapters.");
        }

        foreach (GraphicsAdapterInfo adapter in adapters)
        {
            _ = message.Append(CultureInfo.InvariantCulture, $" Adapter {adapter.Index} '{adapter.Name}': ")
                .Append(adapter.RejectionReasons.Count == 0 ? "suitable" : string.Join("; ", adapter.RejectionReasons))
                .Append('.');
        }

        index = -1;
        error = new EngineError(ErrorKind.Unsupported, message.ToString());
        return false;
    }

    // The preference among suitable adapters, highest first (docs/design/graphics.md section 2, "Adapter selection").
    private static int Preference(AdapterType type) => type switch
    {
        AdapterType.Discrete => 4,
        AdapterType.Integrated => 3,
        AdapterType.Virtual => 2,
        AdapterType.Cpu => 1,
        _ => 0,
    };

    private static bool Matches(GraphicsAdapterInfo adapter, AdapterSelection selection) => selection.Kind switch
    {
        AdapterSelectionKind.Index => adapter.Index == selection.Index,
        AdapterSelectionKind.Name => adapter.Name.Contains(selection.Name!, StringComparison.OrdinalIgnoreCase),
        AdapterSelectionKind.Type => adapter.Type == selection.Type,
        _ => true,
    };

    private static void LogRejections(LogCategory log, IReadOnlyList<GraphicsAdapterInfo> adapters)
    {
        foreach (GraphicsAdapterInfo adapter in adapters)
        {
            if (adapter.RejectionReasons.Count > 0)
            {
                log.Information($"Adapter {adapter.Index} '{adapter.Name}' rejected: {string.Join("; ", adapter.RejectionReasons)}.");
            }
        }
    }

    // The live objects the leak report lists: every kind's, and the swapchains.
    private int LiveObjectCount => _handles.LiveCount + _swapchainHandles.Count;

    // One "Kind 'DebugName'" line per live object and swapchain, oldest first.
    private List<string> LeakLines()
    {
        List<HandleTables.LiveObject> objects = _handles.ListLive();
        var lines = new List<(long Sequence, string Line)>(objects.Count + _swapchainHandles.Count);
        foreach (HandleTables.LiveObject live in objects)
        {
            lines.Add((live.Sequence, $"{live.Kind} '{live.DebugName}'"));
        }

        AddSwapchainLeaks(lines);
        lines.Sort(static (a, b) => a.Sequence.CompareTo(b.Sequence));
        return lines.ConvertAll(static line => line.Line);
    }

    private void ThrowIfUnusable(string apiName)
    {
        _owner.ThrowIfNotCurrentThread(apiName);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
