using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;

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
/// <b>Allocation.</b> <see cref="Create"/> and <see cref="EnumerateAdapters"/> allocate. <see cref="Adapter"/>,
/// <see cref="Capabilities"/> and <see cref="GetFormatSupport"/> allocate nothing, except an exception they throw.
/// </para>
/// </remarks>
public sealed partial class GraphicsDevice : IDisposable
{
    private const string LogCategoryName = "Graphics";

    private readonly IBackendDevice _backend;
    private readonly ThreadAffinity _owner;
    private bool _disposed;

    private GraphicsDevice(IBackendDevice backend, ThreadAffinity owner)
    {
        _backend = backend;
        _owner = owner;
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

        Result<IReadOnlyList<GraphicsAdapterInfo>> adapters = backend.EnumerateAdapters(request);
        if (!adapters.TryGetValue(out IReadOnlyList<GraphicsAdapterInfo>? list))
        {
            return adapters.Error;
        }

        LogRejections(options.Logger.CreateCategory(LogCategoryName), list);
        if (!TrySelect(list, options.Adapter, out int adapterIndex, out error))
        {
            return error;
        }

        Result<IBackendDevice> created = backend.CreateDevice(request, adapterIndex, new BackendObjectTable());
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

        return new GraphicsDevice(device, ThreadAffinity.CurrentThread());
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

        return _backend.GetFormatSupport(format);
    }

    /// <summary>Disposes the backend device, once; later calls do nothing.</summary>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread, also when already disposed.</exception>
    /// <remarks>
    /// Afterwards every member except <see cref="Dispose"/>, <see cref="Adapter"/> and <see cref="Capabilities"/> throws
    /// <see cref="ObjectDisposedException"/>. The backend, logger, allocator and compatible window are left alone.
    /// </remarks>
    public void Dispose()
    {
        _owner.ThrowIfNotCurrentThread(nameof(GraphicsDevice) + "." + nameof(Dispose));
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _backend.Dispose();
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

    private void ThrowIfUnusable(string apiName)
    {
        _owner.ThrowIfNotCurrentThread(apiName);
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
