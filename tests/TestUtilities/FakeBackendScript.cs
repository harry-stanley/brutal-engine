using System;
using System.Collections.Generic;
using System.Globalization;
using BrutalEngine.Core;
using BrutalEngine.Graphics;
using BrutalEngine.Graphics.Backend;
using Support = BrutalEngine.Graphics.FormatSupport;

namespace BrutalEngine.TestUtilities;

/// <summary>
/// What a <see cref="FakeGraphicsBackend"/> and its devices report: the adapters, capabilities and format support, and
/// one-shot failures of named operations. Tests change it freely between calls.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads.</b> <see cref="FakeGraphicsBackend.EnumerateAdapters"/> copies <see cref="Adapters"/> when it runs. A device
/// takes <see cref="Capabilities"/> when it is created, and calls <see cref="FormatSupport"/> on each
/// <see cref="FakeBackendDevice.GetFormatSupport"/>, so a test that changes it mid-device breaks the "constant for the
/// device's lifetime" rule on purpose. Scripted failures are shared by the backend and every device it creates.
/// </para>
/// <para><b>Threading.</b> Not thread-safe: the test's thread only, like the backend contract.</para>
/// </remarks>
public sealed class FakeBackendScript
{
    private const int OperationCount = (int)FakeBackendOperation.Dispose + 1;

    private readonly EngineError?[] _failures = new EngineError?[OperationCount];
    private readonly (SwapchainStatus Status, int NativeCode)?[] _statuses = new (SwapchainStatus, int)?[OperationCount];
    private GraphicsCapabilities _capabilities = DefaultCapabilities;
    private Func<TextureFormat, Support> _formatSupport = DefaultFormatSupport;
    private Func<BackendSwapchainRequest, SwapchainInfo> _swapchainInfo = DefaultSwapchainInfo;
    private Func<long, int, ulong> _timestampTicks = DefaultTimestampTicks;

    /// <summary>Creates a script with one suitable adapter (<see cref="CreateAdapter"/> at index 0) and the defaults.</summary>
    public FakeBackendScript() => Adapters = [CreateAdapter(0)];

    /// <summary>
    /// The capabilities a new script reports: no optional features and limits of a typical desktop GPU (8192 texels, 256
    /// array layers, 4 bind groups, 128 push-constant bytes, 8 color targets, 256-byte offset alignments, 16x anisotropy,
    /// 1x and 4x color samples, 1 ns per timestamp tick).
    /// </summary>
    public static GraphicsCapabilities DefaultCapabilities { get; } = new()
    {
        Features = GraphicsFeatures.None,
        Limits = new GraphicsLimits
        {
            MaxTextureDimension2D = 8192,
            MaxTextureArrayLayers = 256,
            MaxBindGroups = 4,
            MaxPushConstantBytes = 128,
            MaxColorTargets = 8,
            MinUniformBufferOffsetAlignment = 256,
            MinStorageBufferOffsetAlignment = 256,
            MaxSamplerAnisotropy = 16,
            ColorSampleCounts = SampleCounts.X1 | SampleCounts.X4,
            TimestampPeriodNanoseconds = 1,
        },
    };

    /// <summary>The adapters <see cref="FakeGraphicsBackend.EnumerateAdapters"/> reports, in order.</summary>
    /// <remarks>
    /// Each adapter's <see cref="GraphicsAdapterInfo.Index"/> must equal its position, as the contract requires; the fake
    /// throws <see cref="InvalidOperationException"/> at enumeration when the script breaks that.
    /// </remarks>
    public List<GraphicsAdapterInfo> Adapters { get; }

    /// <summary>The capabilities each new device reports; <see cref="DefaultCapabilities"/> at first.</summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public GraphicsCapabilities Capabilities
    {
        get => _capabilities;
        set => _capabilities = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// What <see cref="FakeBackendDevice.GetFormatSupport"/> returns for a format; <see cref="DefaultFormatSupport"/> at
    /// first. It must not allocate if the test measures allocations.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public Func<TextureFormat, Support> FormatSupport
    {
        get => _formatSupport;
        set => _formatSupport = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// What <see cref="FakeBackendDevice.CreateSwapchain"/> reports for a request; <see cref="DefaultSwapchainInfo"/> at
    /// first. The image count it returns must be at least 1.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public Func<BackendSwapchainRequest, SwapchainInfo> SwapchainInfo
    {
        get => _swapchainInfo;
        set => _swapchainInfo = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Whether devices append each call to <see cref="FakeBackendDevice.Calls"/>; on at first. Turn it off, with
    /// <see cref="RecordPlans"/>, before measuring the front end's allocations: recording allocates as the list grows.
    /// </summary>
    public bool RecordCalls { get; set; } = true;

    /// <summary>
    /// Whether <see cref="FakeBackendDevice.Execute"/> copies each plan into <see cref="FakeBackendDevice.Plans"/>; off at
    /// first. Copying allocates.
    /// </summary>
    public bool RecordPlans { get; set; }

    /// <summary>
    /// The tick <see cref="FakeBackendDevice.TryReadTimestamps"/> reports for a frame number and query index;
    /// <see cref="DefaultTimestampTicks"/> at first. It must not allocate if the test measures allocations.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is <see langword="null"/>.</exception>
    public Func<long, int, ulong> TimestampTicks
    {
        get => _timestampTicks;
        set => _timestampTicks = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Describes a suitable fake adapter: a discrete GPU named "Fake GPU &lt;index&gt;" with no rejection reasons.</summary>
    /// <param name="index">The adapter's position in <see cref="Adapters"/>.</param>
    /// <param name="rejectionReasons">Why the adapter can't serve the request; none by default.</param>
    /// <returns>The adapter description.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is negative.</exception>
    public static GraphicsAdapterInfo CreateAdapter(int index, params string[] rejectionReasons)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentNullException.ThrowIfNull(rejectionReasons);
        return new GraphicsAdapterInfo
        {
            Index = index,
            Name = string.Create(CultureInfo.InvariantCulture, $"Fake GPU {index}"),
            Type = AdapterType.Discrete,
            Driver = "Fake",
            ApiVersion = "Fake 1.0",
            RejectionReasons = rejectionReasons,
        };
    }

    /// <summary>
    /// The format support a new script reports: depth formats can be sampled, copied and used as depth targets; every
    /// other format can be sampled, filtered, copied, rendered to and blended; <see cref="TextureFormat.Undefined"/> has none.
    /// </summary>
    /// <param name="format">The format.</param>
    /// <returns>The supported uses.</returns>
    /// <remarks>Allocates nothing.</remarks>
    public static Support DefaultFormatSupport(TextureFormat format) => format switch
    {
        TextureFormat.Undefined => Support.None,
        TextureFormat.Depth16Unorm or TextureFormat.Depth32Float =>
            Support.Sampled | Support.DepthStencilTarget | Support.CopySource | Support.CopyDestination,
        _ => Support.Sampled | Support.Filterable | Support.ColorTarget | Support.Blendable | Support.CopySource
            | Support.CopyDestination,
    };

    /// <summary>
    /// The swapchain a new script reports: the window's pixel size, the requested present mode, 3 images that support copies,
    /// and <see cref="TextureFormat.Bgra8UnormSrgb"/> or <see cref="TextureFormat.Bgra8Unorm"/> for the requested format.
    /// </summary>
    /// <param name="request">The swapchain request.</param>
    /// <returns>What the swapchain really is.</returns>
    public static SwapchainInfo DefaultSwapchainInfo(BackendSwapchainRequest request)
    {
        TextureFormat format = request.Options.Format == SwapchainFormat.Srgb ? TextureFormat.Bgra8UnormSrgb : TextureFormat.Bgra8Unorm;
        return new SwapchainInfo(request.Source.PixelWidth, request.Source.PixelHeight, format, request.Options.PresentMode, 3, true);
    }

    /// <summary>The ticks a new script reports: 1000 per query index, so each labelled pass takes 1000 ticks.</summary>
    /// <param name="frameNumber">The frame's number.</param>
    /// <param name="queryIndex">The query index.</param>
    /// <returns>The tick count.</returns>
    public static ulong DefaultTimestampTicks(long frameNumber, int queryIndex) => (ulong)queryIndex * 1000;

    /// <summary>Makes the next call of an operation fail with an error, once; the call after it behaves normally again.</summary>
    /// <param name="operation">
    /// An operation that returns a <see cref="Result"/>: <see cref="FakeBackendOperation.EnumerateAdapters"/>,
    /// <see cref="FakeBackendOperation.CreateDevice"/>, the creation operations, <see cref="FakeBackendOperation.WaitForFrame"/>,
    /// <see cref="FakeBackendOperation.IsFrameComplete"/>, <see cref="FakeBackendOperation.BeginFrame"/>,
    /// <see cref="FakeBackendOperation.Execute"/> or <see cref="FakeBackendOperation.WaitIdle"/>.
    /// </param>
    /// <param name="error">The error to return, for example <see cref="ErrorKind.Timeout"/> or a device loss.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="operation"/> isn't a defined member.</exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> doesn't return a <see cref="Result"/>.</exception>
    /// <exception cref="InvalidOperationException">A failure of that operation is already pending.</exception>
    /// <remarks>
    /// The failure is taken by the next call of that operation on the backend or any of its devices, before the call has any
    /// effect, so a failed creation creates nothing. Allocates nothing.
    /// </remarks>
    public void FailNext(FakeBackendOperation operation, EngineError error)
    {
        if ((uint)operation >= OperationCount)
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not a FakeBackendOperation member.");
        }

        if (!ReturnsResult(operation))
        {
            throw new ArgumentException($"{operation} doesn't return a Result, so it can't fail.", nameof(operation));
        }

        if (_failures[(int)operation] is not null)
        {
            throw new InvalidOperationException($"A failure of {operation} is already pending.");
        }

        _failures[(int)operation] = error;
    }

    /// <summary>Makes the next call of a swapchain operation report a status, once; the call after it succeeds again.</summary>
    /// <param name="operation"><see cref="FakeBackendOperation.AcquireImage"/> or <see cref="FakeBackendOperation.Present"/>.</param>
    /// <param name="status">The status to report.</param>
    /// <param name="nativeCode">The native code to report with it.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="operation"/> or <paramref name="status"/> isn't a defined member.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="operation"/> isn't one of the two swapchain operations.</exception>
    /// <exception cref="InvalidOperationException">A status for that operation is already pending.</exception>
    /// <remarks>
    /// The status is taken by the next call of that operation that passes its checks. An acquire that reports a status other
    /// than <see cref="SwapchainStatus.Success"/> or <see cref="SwapchainStatus.Suboptimal"/> acquires no image. Allocates
    /// nothing.
    /// </remarks>
    public void SwapchainStatusNext(FakeBackendOperation operation, SwapchainStatus status, int nativeCode)
    {
        if ((uint)operation >= OperationCount)
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Not a FakeBackendOperation member.");
        }

        if ((uint)status > (uint)SwapchainStatus.DeviceLost)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Not a SwapchainStatus member.");
        }

        if (operation is not (FakeBackendOperation.AcquireImage or FakeBackendOperation.Present))
        {
            throw new ArgumentException($"{operation} doesn't report a swapchain status.", nameof(operation));
        }

        if (_statuses[(int)operation] is not null)
        {
            throw new InvalidOperationException($"A status of {operation} is already pending.");
        }

        _statuses[(int)operation] = (status, nativeCode);
    }

    /// <summary>Takes the pending status of a swapchain operation, if any.</summary>
    internal bool TakeStatus(FakeBackendOperation operation, out SwapchainStatus status, out int nativeCode)
    {
        (SwapchainStatus Status, int NativeCode)? pending = _statuses[(int)operation];
        _statuses[(int)operation] = null;
        (status, nativeCode) = pending.GetValueOrDefault();
        return pending.HasValue;
    }

    /// <summary>Takes the pending failure of an operation, if any.</summary>
    internal bool TakeFailure(FakeBackendOperation operation, out EngineError error)
    {
        EngineError? pending = _failures[(int)operation];
        _failures[(int)operation] = null;
        error = pending.GetValueOrDefault();
        return pending.HasValue;
    }

    private static bool ReturnsResult(FakeBackendOperation operation) => operation is FakeBackendOperation.EnumerateAdapters
        or FakeBackendOperation.CreateDevice or FakeBackendOperation.CreateBuffer or FakeBackendOperation.CreateTexture
        or FakeBackendOperation.CreateTextureView or FakeBackendOperation.CreateSampler or FakeBackendOperation.CreateSurface
        or FakeBackendOperation.CreateSwapchain or FakeBackendOperation.WaitForFrame or FakeBackendOperation.IsFrameComplete
        or FakeBackendOperation.BeginFrame or FakeBackendOperation.Execute or FakeBackendOperation.WaitIdle;
}
