using System;
using System.Globalization;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Memory;

namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// What the front end asks a backend for when it enumerates adapters or creates a device: the backend's share of
/// <see cref="GraphicsDeviceOptions"/> (docs/design/graphics.md, "The backend interface").
/// </summary>
/// <remarks>
/// <para>
/// Built by the front end from options it has already validated, so a backend may assume <see cref="FramesInFlight"/> is
/// from 1 to 3, <see cref="GpuTimeout"/> is positive, the feature sets are <see cref="GraphicsFeatures"/> members that
/// don't overlap, and <see cref="ApplicationName"/> isn't empty. The defaults of the properties without one in the options
/// (0 frames in flight, a zero timeout, an empty name) describe no usable device and never reach a backend.
/// </para>
/// <para>
/// The request holds <see cref="Logger"/>, <see cref="Allocator"/> and <see cref="CompatibleSurface"/> without owning them:
/// the front end keeps them alive until the backend device is disposed, and the backend never disposes them. Equality
/// compares every property with its type's default equality: <see cref="Logger"/> and <see cref="CompatibleSurface"/>
/// by reference, <see cref="Allocator"/> by reference unless its implementation overrides
/// <see cref="object.Equals(object)"/>, and <see cref="ApplicationName"/> ordinally. An immutable record, safe to share
/// between threads; reading it allocates nothing.
/// </para>
/// </remarks>
public sealed record BackendDeviceRequest
{
    /// <summary>
    /// The engine logger the backend writes to, under the categories <c>"Graphics"</c> and <c>"Graphics.Validation"</c>
    /// (validation layer messages).
    /// </summary>
    public required Logger Logger { get; init; }

    /// <summary>
    /// The allocator for every byte of the backend's unmanaged memory (GPU memory excepted), used only on the owner thread.
    /// Tests pass a <see cref="TrackingAllocator"/> and check it for leaks after disposing the device.
    /// </summary>
    public required INativeAllocator Allocator { get; init; }

    /// <summary>
    /// The window the device will present to, or <see langword="null"/> for an offscreen-only device: the only link to a
    /// window a backend gets, never the window itself.
    /// </summary>
    /// <remarks>
    /// The backend enables its required instance extensions, creates its surface to check each adapter's presentation
    /// support, and keeps that surface for the first <see cref="IBackendDevice.CreateSurface"/> for this same instance. The
    /// front end passes this same instance in the window's swapchain requests.
    /// </remarks>
    public WindowSurfaceSource? CompatibleSurface { get; init; }

    /// <summary>
    /// Whether to enable the backend's validation layer, synchronization validation included, and count its messages in
    /// <see cref="IBackendDevice.ValidationErrors"/> and <see cref="IBackendDevice.ValidationWarnings"/>.
    /// </summary>
    public bool Validation { get; init; }

    /// <summary>
    /// The number of frame slots, from 1 to 3: every frame-slot argument the device receives is from 0 to this value
    /// minus 1, and each slot has its own completion signal and command, descriptor and query pools.
    /// </summary>
    public int FramesInFlight { get; init; }

    /// <summary>The features the adapter must have: an adapter without them is rejected, with the reason.</summary>
    public GraphicsFeatures RequiredFeatures { get; init; }

    /// <summary>The features to enable when the adapter has them; reported in <see cref="IBackendDevice.Capabilities"/>.</summary>
    public GraphicsFeatures OptionalFeatures { get; init; }

    /// <summary>
    /// The longest any wait for the GPU may block: <see cref="IBackendDevice.WaitForFrame"/> and every internal fence wait
    /// return <see cref="Core.ErrorKind.Timeout"/> after it, which the front end treats as a hung GPU.
    /// </summary>
    public TimeSpan GpuTimeout { get; init; }

    /// <summary>The application name the backend reports to the driver (for Vulkan, the instance's application info).</summary>
    public string ApplicationName { get; init; } = "";

    /// <summary>
    /// Returns every property, for example <c>BackendDeviceRequest { Logger = BrutalEngine.Core.Logging.Logger, ...,
    /// FramesInFlight = 2, ..., GpuTimeout = 00:00:10, ApplicationName = BrutalEngine }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is. Reference-typed
    /// properties print their type name, or nothing when <see langword="null"/>. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"BackendDeviceRequest {{ Logger = {Logger}, Allocator = {Allocator}, CompatibleSurface = {CompatibleSurface}, Validation = {Validation}, "
        + $"FramesInFlight = {FramesInFlight}, RequiredFeatures = {RequiredFeatures}, OptionalFeatures = {OptionalFeatures}, "
        + $"GpuTimeout = {GpuTimeout}, ApplicationName = {ApplicationName} }}");
}
