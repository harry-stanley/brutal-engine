using System.Collections.Generic;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// A graphics backend: the entry point that enumerates adapters and creates backend devices (docs/design/graphics.md,
/// "The backend interface"). Implemented by backend packages, such as the Vulkan backend; called only by the graphics
/// device's front end, never by games.
/// </summary>
/// <remarks>
/// <para>
/// <b>Ownership.</b> The host creates and keeps the backend. It holds no native state, so it needs no disposal, and the
/// front end neither takes ownership of it nor disposes it, whether device creation succeeds or fails. Every native
/// object lives in an <see cref="IBackendDevice"/>.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Name"/> is read on any thread. <see cref="EnumerateAdapters"/> and
/// <see cref="CreateDevice"/> are called on the thread that will own the device (the thread that calls
/// <c>GraphicsDevice.Create</c>), never re-entrantly; a backend may run several of them in turn, and each call stands
/// alone.
/// </para>
/// <para>
/// <b>Errors.</b> Expected failures come back as a failed <see cref="Result{T}"/>, never as an exception: no backend runtime
/// (<see cref="ErrorKind.Unavailable"/>, for example a missing Vulkan loader), validation requested but its layer missing
/// (<see cref="ErrorKind.Unavailable"/>), out of memory, or any other native failure with its code. The front end validates
/// the request before either call, so a backend may assume a valid one.
/// </para>
/// </remarks>
public interface IGraphicsBackend
{
    /// <summary>The backend's display name, for logs and diagnostics, for example <c>"Vulkan"</c>.</summary>
    /// <remarks>Constant for the backend's lifetime. Any thread. Allocates nothing.</remarks>
    public string Name { get; }

    /// <summary>
    /// Lists every adapter the backend can see, each with the reasons it can't serve the request (empty when it can).
    /// </summary>
    /// <param name="request">
    /// The device request: its <see cref="BackendDeviceRequest.CompatibleSurface"/>, required features and validation
    /// decide each adapter's rejection reasons.
    /// </param>
    /// <returns>
    /// The adapters in the backend's enumeration order, with <see cref="GraphicsAdapterInfo.Index"/> equal to the position
    /// in the list; an empty list or one with only rejected adapters is a success. Fails as described on the interface, for
    /// example when there is no backend runtime.
    /// </returns>
    /// <remarks>
    /// Owner thread. Creates and destroys whatever native state it needs within the call; nothing outlives it. Allocates.
    /// </remarks>
    public Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(BackendDeviceRequest request);

    /// <summary>Creates a backend device on one adapter.</summary>
    /// <param name="request">The device request, already validated.</param>
    /// <param name="adapterIndex">
    /// The chosen adapter's <see cref="GraphicsAdapterInfo.Index"/>, from an enumeration with the same request: the front
    /// end applies the adapter selection and passes only a suitable adapter.
    /// </param>
    /// <param name="objects">
    /// The front end's handle table for this device, which the backend reads to resolve handles inside descriptions and
    /// labels in the command stream. Owned by the front end; the backend keeps a reference but never writes to it.
    /// </param>
    /// <returns>
    /// The device, owned by the caller from then on. On failure nothing is left alive: the backend destroys whatever it
    /// created, including any surface it made for <see cref="BackendDeviceRequest.CompatibleSurface"/>. An adapter that
    /// disappeared since the enumeration fails with <see cref="ErrorKind.Unavailable"/>.
    /// </returns>
    /// <remarks>
    /// Owner thread. When <see cref="BackendDeviceRequest.CompatibleSurface"/> is set, the backend enables its instance
    /// extensions and creates the surface it checks presentation against; the device keeps that surface for the first
    /// <see cref="IBackendDevice.CreateSurface"/> for that same source. To recover from device loss the front end disposes
    /// the lost device and calls it again with the same table, after clearing the table's object entries. Allocates.
    /// </remarks>
    public Result<IBackendDevice> CreateDevice(BackendDeviceRequest request, int adapterIndex, BackendObjectTable objects);
}
