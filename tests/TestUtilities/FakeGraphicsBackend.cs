using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.TestUtilities;

/// <summary>
/// An in-memory <see cref="IGraphicsBackend"/> for unit tests (docs/design/graphics.md, "Architecture"): it reports the
/// adapters its <see cref="Script"/> lists and creates <see cref="FakeBackendDevice"/> instances, so the front end's device
/// rules run on any OS without a GPU.
/// </summary>
/// <remarks>
/// <para>
/// <b>Contract checks.</b> Misuse the contract says can't reach a backend (an adapter index outside the enumeration, a
/// rejected adapter, frames in flight outside 1 to 3) throws <see cref="InvalidOperationException"/> with a message that starts
/// "contract violation:", so front-end tests catch the bug. Null arguments throw <see cref="ArgumentNullException"/>.
/// </para>
/// <para>
/// <b>Ownership and threading.</b> Holds no native state and needs no disposal, like a real backend. Not thread-safe: the
/// test's thread only.
/// </para>
/// </remarks>
public sealed class FakeGraphicsBackend : IGraphicsBackend
{
    /// <summary>Creates a backend that reports what <paramref name="script"/> says.</summary>
    /// <param name="script">The script, kept by reference: later changes apply to later calls.</param>
    /// <exception cref="ArgumentNullException"><paramref name="script"/> is <see langword="null"/>.</exception>
    public FakeGraphicsBackend(FakeBackendScript script)
    {
        ArgumentNullException.ThrowIfNull(script);
        Script = script;
    }

    /// <summary>The script this backend and its devices follow.</summary>
    public FakeBackendScript Script { get; }

    /// <summary>The most recent device <see cref="CreateDevice"/> created, disposed or not; <see langword="null"/> before the first.</summary>
    public FakeBackendDevice? LastDevice { get; private set; }

    /// <inheritdoc/>
    /// <remarks>Always <c>"Fake"</c>.</remarks>
    public string Name => "Fake";

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">An adapter in the script has an index other than its position.</exception>
    /// <remarks>Takes a pending <see cref="FakeBackendOperation.EnumerateAdapters"/> failure first. Returns a copy of the list.</remarks>
    public Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(BackendDeviceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        for (int i = 0; i < Script.Adapters.Count; i++)
        {
            if (Script.Adapters[i].Index != i)
            {
                throw new InvalidOperationException(
                    $"The script's adapter at position {i} has index {Script.Adapters[i].Index}; each index must equal its position.");
            }
        }

        if (Script.TakeFailure(FakeBackendOperation.EnumerateAdapters, out EngineError error))
        {
            return error;
        }

        // Interface-typed results take no implicit conversion, so both are built explicitly.
        return new Result<IReadOnlyList<GraphicsAdapterInfo>>(Script.Adapters.ToArray());
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// A contract violation: <paramref name="adapterIndex"/> isn't a scripted adapter, the adapter has rejection reasons, or
    /// the request's frames in flight isn't from 1 to 3.
    /// </exception>
    /// <remarks>
    /// The checks run first, then a pending <see cref="FakeBackendOperation.CreateDevice"/> failure is taken, which creates
    /// nothing. A device created on success becomes <see cref="LastDevice"/>.
    /// </remarks>
    public Result<IBackendDevice> CreateDevice(BackendDeviceRequest request, int adapterIndex, BackendObjectTable objects)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(objects);
        if ((uint)adapterIndex >= (uint)Script.Adapters.Count)
        {
            throw new InvalidOperationException($"contract violation: CreateDevice with adapter index {adapterIndex}, not a scripted adapter.");
        }

        GraphicsAdapterInfo adapter = Script.Adapters[adapterIndex];
        if (adapter.RejectionReasons.Count > 0)
        {
            throw new InvalidOperationException($"contract violation: CreateDevice on adapter {adapterIndex}, which is rejected.");
        }

        if (request.FramesInFlight is < 1 or > 3)
        {
            throw new InvalidOperationException($"contract violation: CreateDevice with {request.FramesInFlight} frames in flight.");
        }

        if (Script.TakeFailure(FakeBackendOperation.CreateDevice, out EngineError error))
        {
            return error;
        }

        var device = new FakeBackendDevice(Script, request, adapter, objects);
        LastDevice = device;
        return new Result<IBackendDevice>(device);
    }
}
