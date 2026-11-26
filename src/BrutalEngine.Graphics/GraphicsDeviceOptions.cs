using System;
using System.Globalization;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Memory;
using BrutalEngine.Platform;

namespace BrutalEngine.Graphics;

/// <summary>The settings a graphics device is created with (docs/design/graphics.md section 2).</summary>
/// <remarks>
/// <para>
/// The record only describes the device; device creation checks it (docs/design/graphics.md section 2). A
/// <see langword="null"/> <see cref="Logger"/>, <see cref="Allocator"/> or <see cref="ApplicationName"/>, an empty
/// <see cref="ApplicationName"/>, features that are both required and optional, or an
/// <see cref="UploadRingBytesPerFrame"/> that isn't a multiple of 256 throws <see cref="ArgumentException"/>. A
/// <see cref="FramesInFlight"/> outside 1 to 3, feature flags that aren't <see cref="GraphicsFeatures"/> members, an
/// <see cref="UploadRingBytesPerFrame"/> below 65536 (64 KiB), or a <see cref="GpuTimeout"/> that isn't positive throws
/// <see cref="ArgumentOutOfRangeException"/>. Each exception's message starts with the option's name, and its parameter
/// is <c>options</c>.
/// </para>
/// <para>
/// Equality compares every property with its type's default equality: <see cref="Logger"/> by reference,
/// <see cref="Allocator"/> and <see cref="CompatibleWindow"/> by reference unless their implementations override
/// <see cref="object.Equals(object)"/>, and <see cref="ApplicationName"/> ordinally. An immutable record: the device
/// copies the values it needs when it is created, so a later <c>with</c> copy has no effect on it. Safe to share
/// between threads. Creating one allocates (the default <see cref="Allocator"/> is a new <see cref="HeapAllocator"/>);
/// reading it doesn't.
/// </para>
/// </remarks>
public sealed record GraphicsDeviceOptions
{
    // The design note gives no minimum, so the issue chose 64 KiB: one 128 by 128 RGBA8 block (512-byte rows, a multiple
    // of the 256-byte TextureBufferCopy.RowPitchAlignment), so a ring always fits a small texture upload.
    private const long MinUploadRingBytesPerFrame = 64 * 1024;
    private const long UploadRingAlignment = 256;

    private const GraphicsFeatures AllFeatures = GraphicsFeatures.SamplerAnisotropy | GraphicsFeatures.TimestampQueries
        | GraphicsFeatures.TextureCompressionBC | GraphicsFeatures.TextureCompressionAstc;

    /// <summary>
    /// The engine logger the device writes to, under the categories <c>"Graphics"</c> and <c>"Graphics.Validation"</c>
    /// (the backend's validation messages). Required; must not be <see langword="null"/>.
    /// </summary>
    /// <remarks>Not owned: the device never disposes it, and the caller keeps it alive until the device is disposed.</remarks>
    public required Logger Logger { get; init; }

    /// <summary>
    /// The allocator for the device's unmanaged memory: command streams, queues and tracking. Default a new
    /// <see cref="HeapAllocator"/>; must not be <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Not owned. The device uses it from its owner thread only, so it needn't be thread-safe unless shared. Tests pass
    /// a <see cref="TrackingAllocator"/> and check it for leaks after disposing the device.
    /// </remarks>
    public INativeAllocator Allocator { get; init; } = new HeapAllocator();

    /// <summary>
    /// The window the device will present to, or <see langword="null"/> (the default) for an offscreen-only device.
    /// </summary>
    /// <remarks>
    /// The backend enables the window's surface extensions and selects an adapter that can present to it
    /// (docs/design/graphics.md section 3). The device keeps the surface it creates for selection until the first
    /// swapchain for this window takes it over, so the window must outlive the device. A window without a Vulkan
    /// surface makes device creation fail with <see cref="Core.ErrorKind.Unsupported"/>. Not owned.
    /// </remarks>
    public IWindow? CompatibleWindow { get; init; }

    /// <summary>
    /// Which adapter (GPU) to use. Default <see cref="AdapterSelection.Auto"/>: the best suitable adapter. An override
    /// that matches no suitable adapter fails device creation; it never falls back.
    /// </summary>
    public AdapterSelection Adapter { get; init; } = AdapterSelection.Auto;

    /// <summary>
    /// Whether to enable the backend's validation layer, reporting misuse under <c>"Graphics.Validation"</c>. Default
    /// <see langword="false"/>; the host turns it on in debug builds, and tests always do.
    /// </summary>
    public bool Validation { get; init; }

    /// <summary>
    /// How many frames the CPU may record ahead of the GPU, each with its own fence, recording memory, upload ring slice
    /// and deletion queue. Default 2; valid from 1 to 3.
    /// </summary>
    public int FramesInFlight { get; init; } = 2;

    /// <summary>
    /// The features the adapter must have; an adapter without them is rejected. Default <see cref="GraphicsFeatures.None"/>.
    /// Only <see cref="GraphicsFeatures"/> members, and no flag shared with <see cref="OptionalFeatures"/>.
    /// </summary>
    public GraphicsFeatures RequiredFeatures { get; init; }

    /// <summary>
    /// The features to enable when the adapter has them. Default <see cref="GraphicsFeatures.None"/>. Only
    /// <see cref="GraphicsFeatures"/> members, and no flag shared with <see cref="RequiredFeatures"/>.
    /// </summary>
    public GraphicsFeatures OptionalFeatures { get; init; }

    /// <summary>
    /// The upload ring's size per frame in flight, in bytes (docs/design/graphics.md section 6). Default 8 MiB; at least
    /// 65536 (64 KiB) and a multiple of 256.
    /// </summary>
    /// <remarks>A write that doesn't fit gets a dedicated block for its frame, which is slower but still correct.</remarks>
    public long UploadRingBytesPerFrame { get; init; } = 8 << 20;

    /// <summary>
    /// The longest the device waits for the GPU at any fence: a frame this late is a hung GPU, treated as device loss.
    /// Default 10 seconds; must be positive.
    /// </summary>
    public TimeSpan GpuTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The application name the backend reports to the driver (for Vulkan, the instance's application info). Default
    /// <c>"BrutalEngine"</c>; must not be <see langword="null"/> or empty.
    /// </summary>
    public string ApplicationName { get; init; } = "BrutalEngine";

    /// <summary>
    /// Returns every property, for example <c>GraphicsDeviceOptions { Logger = BrutalEngine.Core.Logging.Logger, ...,
    /// FramesInFlight = 2, ..., GpuTimeout = 00:00:10, ApplicationName = BrutalEngine }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is. Reference-typed
    /// options print their type name, or nothing when <see langword="null"/>. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"GraphicsDeviceOptions {{ Logger = {Logger}, Allocator = {Allocator}, CompatibleWindow = {CompatibleWindow}, Adapter = {Adapter}, "
        + $"Validation = {Validation}, FramesInFlight = {FramesInFlight}, RequiredFeatures = {RequiredFeatures}, OptionalFeatures = {OptionalFeatures}, "
        + $"UploadRingBytesPerFrame = {UploadRingBytesPerFrame}, GpuTimeout = {GpuTimeout}, ApplicationName = {ApplicationName} }}");

    /// <summary>Throws if an option breaks its documented rule; returns normally otherwise. Device creation calls it.</summary>
    /// <remarks>Allocates only when it throws.</remarks>
    /// <exception cref="ArgumentException">
    /// <see cref="Logger"/>, <see cref="Allocator"/> or <see cref="ApplicationName"/> is <see langword="null"/>,
    /// <see cref="ApplicationName"/> is empty, <see cref="RequiredFeatures"/> and <see cref="OptionalFeatures"/> share a
    /// flag, or <see cref="UploadRingBytesPerFrame"/> isn't a multiple of 256.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="FramesInFlight"/> is outside 1 to 3, <see cref="RequiredFeatures"/> or <see cref="OptionalFeatures"/>
    /// has a flag that isn't a <see cref="GraphicsFeatures"/> member, <see cref="UploadRingBytesPerFrame"/> is below
    /// 65536, or <see cref="GpuTimeout"/> isn't positive.
    /// </exception>
    internal void Validate()
    {
        const string Parameter = "options";
        if (Logger is null)
        {
            throw new ArgumentException("GraphicsDeviceOptions.Logger must not be null.", Parameter);
        }

        if (Allocator is null)
        {
            throw new ArgumentException("GraphicsDeviceOptions.Allocator must not be null.", Parameter);
        }

        if (FramesInFlight is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(Parameter, FramesInFlight, "GraphicsDeviceOptions.FramesInFlight must be from 1 to 3.");
        }

        if ((RequiredFeatures & ~AllFeatures) != 0)
        {
            throw new ArgumentOutOfRangeException(
                Parameter, (long)RequiredFeatures, "GraphicsDeviceOptions.RequiredFeatures has flags that aren't GraphicsFeatures members.");
        }

        if ((OptionalFeatures & ~AllFeatures) != 0)
        {
            throw new ArgumentOutOfRangeException(
                Parameter, (long)OptionalFeatures, "GraphicsDeviceOptions.OptionalFeatures has flags that aren't GraphicsFeatures members.");
        }

        if ((RequiredFeatures & OptionalFeatures) != GraphicsFeatures.None)
        {
            throw new ArgumentException(
                $"GraphicsDeviceOptions.RequiredFeatures and OptionalFeatures must not overlap; both have {RequiredFeatures & OptionalFeatures}.", Parameter);
        }

        if (UploadRingBytesPerFrame < MinUploadRingBytesPerFrame)
        {
            throw new ArgumentOutOfRangeException(
                Parameter, UploadRingBytesPerFrame, "GraphicsDeviceOptions.UploadRingBytesPerFrame must be at least 65536 (64 KiB).");
        }

        if (UploadRingBytesPerFrame % UploadRingAlignment != 0)
        {
            throw new ArgumentException("GraphicsDeviceOptions.UploadRingBytesPerFrame must be a multiple of 256.", Parameter);
        }

        if (GpuTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(Parameter, GpuTimeout, "GraphicsDeviceOptions.GpuTimeout must be positive.");
        }

        if (string.IsNullOrEmpty(ApplicationName))
        {
            throw new ArgumentException("GraphicsDeviceOptions.ApplicationName must not be null or empty.", Parameter);
        }
    }
}
