using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Internal;

namespace BrutalEngine.Graphics;

/// <content>
/// Device loss: the fault seam, the DeviceLost event, resource owners, recovery and the validation canary seam
/// (docs/design/graphics.md sections 12 and 15).
/// </content>
public sealed partial class GraphicsDevice
{
    private const string LostSuffix = ": the device is lost.";
    private const string RecoverApi = nameof(GraphicsDevice) + "." + nameof(Recover);

    private readonly List<GpuOwnerRegistration> _owners = [];
    private DeviceLostEventArgs? _detectedLoss;
    private bool _recovering;

    // True once the backend device is gone: disposed by a recovery that couldn't make a new one, or abandoned after a GPU
    // timeout. Nothing calls it then.
    private bool _backendGone;

    // The validation counts of the backend devices recovery replaced or abandoned, which Statistics adds to the current one's.
    private long _pastValidationErrors;
    private long _pastValidationWarnings;

    /// <summary>
    /// Raised once when the device is lost, synchronously, on the owner thread, as the last step of the call that
    /// detected the loss.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That call is <see cref="BeginFrame"/>, <see cref="SubmitFrame"/>, <see cref="CommandList.AcquireSwapchainImage"/>,
    /// <see cref="WaitIdle"/>, or a creation or write whose backend call reported loss, as
    /// <see cref="DeviceLostEventArgs.Operation"/> names it. When the handlers run, <see cref="IsLost"/> is
    /// <see langword="true"/> and the call has done its work, except in one case: two calls wait for the GPU through
    /// <see cref="WaitIdle"/> first, <see cref="CommandList.AcquireSwapchainImage"/> before it recreates a swapchain and
    /// <see cref="CreateSwapchain"/> while the window's destroyed swapchain still waits for a frame. A loss that nested
    /// wait detects is reported as <c>"WaitIdle"</c>, and the handlers run at the end of that wait, before the outer call
    /// has finished; it then returns <see cref="AcquireStatus.DeviceLost"/> or <see cref="ErrorKind.Unavailable"/>.
    /// </para>
    /// <para>
    /// A handler's exception propagates out of the detecting call (and out of the outer call), and the event isn't raised
    /// again for the loss. Adding or removing a handler is owner thread only and isn't checked.
    /// </para>
    /// </remarks>
    public event EventHandler<DeviceLostEventArgs>? DeviceLost;

    /// <summary>The fault seam (section 12), for the graphics tests and the conformance suite only.</summary>
    internal FaultSeam Faults { get; } = new();

    /// <summary>The number of live owner registrations; for tests.</summary>
    internal int RegisteredOwnerCount => _owners.Count;

    private long ValidationErrorCount => _pastValidationErrors + (_backendGone ? 0 : _backend.ValidationErrors);

    private long ValidationWarningCount => _pastValidationWarnings + (_backendGone ? 0 : _backend.ValidationWarnings);

    /// <summary>Registers something that owns GPU resources of this device, so <see cref="Recover"/> has it make them again.</summary>
    /// <param name="owner">The owner. Not owned: the device keeps a reference until the registration is disposed.</param>
    /// <returns>The registration, owned by the caller: dispose it to unregister.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="owner"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// Owners are kept in registration order; the same owner registered twice is two registrations. Owner thread.
    /// Allocates the registration, and the list when it grows.
    /// </remarks>
    public GpuOwnerRegistration RegisterOwner(IGpuResourceOwner owner)
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(RegisterOwner));
        ArgumentNullException.ThrowIfNull(owner);
        var registration = new GpuOwnerRegistration(this, owner);
        _owners.Add(registration);
        return registration;
    }

    /// <summary>
    /// Recovers a lost device: tells the owners, discards every object, makes a new backend device on the same adapter,
    /// gives each swapchain a new surface, then has the owners make their resources again.
    /// </summary>
    /// <returns>
    /// Success once the device and every owner are back. Every failure is fatal for the host (docs/03 3.2):
    /// <see cref="ErrorKind.Timeout"/> after a loss to a GPU timeout, whose backend device is abandoned instead; the
    /// failure of finding the adapter or creating the device, as <see cref="Create"/> reports it, or of creating a surface;
    /// <see cref="ErrorKind.NotFound"/> when the selection no longer gives this adapter with these capabilities; or the
    /// first owner's failure. A failure before the owners are called leaves the device lost without a backend device, and
    /// a later call tries again. After an owner's failure the device isn't lost, and the owners after it aren't called.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The device isn't lost, a frame is open (submit it first), or an owner called it during a recovery. Nothing changed.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// <para>
    /// In order: every owner's <see cref="IGpuResourceOwner.OnDeviceLost"/>, newest registration first; the old backend
    /// device's validation counts go into <see cref="Statistics"/>; every id held for it (deletion queues, upload blocks,
    /// swapchains and surfaces) is dropped without a backend call, and it is disposed; every handle is released, so every
    /// buffer, texture, view, sampler and swapchain image handle is stale; the new device is created; each swapchain gets
    /// a new surface, so its next acquire recreates it in place and reports <see cref="AcquireStatus.Recreated"/>; the
    /// device stops being lost; then every owner's <see cref="IGpuResourceOwner.OnDeviceRecreated"/>, oldest first. The
    /// owners are those registered when the call starts; one whose registration is disposed during it, in its own
    /// callback or another owner's, isn't called after that.
    /// </para>
    /// <para>
    /// What survives: this object, its options, logger, statistics and frame numbers, the owners, the
    /// <see cref="SwapchainHandle"/>s (<see cref="GetSwapchainInfo"/> reports the old swapchain until the next acquire) and
    /// the <see cref="DebugLabel"/>s. Writes not yet submitted are dropped. An owner's exception propagates, leaving the
    /// device as far as the recovery got.
    /// </para>
    /// <para>
    /// After a loss to a GPU timeout (<see cref="DeviceLostEventArgs.TimedOut"/>) nothing waits for or destroys anything:
    /// the backend device is abandoned with one <see cref="Core.Logging.LogLevel.Error"/> line, no backend call is made,
    /// and every call returns <see cref="ErrorKind.Timeout"/>. Owner thread. Allocates.
    /// </para>
    /// </remarks>
    public Result Recover()
    {
        ThrowIfUnusable(RecoverApi);
        if (!IsLostCore || _frameOpen || _recovering)
        {
            throw new InvalidOperationException(!IsLostCore ? "Recover: the device isn't lost."
                : _frameOpen ? $"Recover: frame {_frameNumber} is open; submit it first." : "Recover: a recovery is already running.");
        }

        if (LostTimedOut)
        {
            Abandon();
            return new EngineError(ErrorKind.Timeout, "Recover: the device was lost to a GPU timeout; its backend device is abandoned.", LostNativeCode);
        }

        _recovering = true;
        try
        {
            // The owners registered when the recovery starts; one unregistered since (by itself or by another owner's
            // callback) isn't called again.
            GpuOwnerRegistration[] owners = [.. _owners];
            for (int i = owners.Length - 1; i >= 0; i--)
            {
                if (_owners.Contains(owners[i]))
                {
                    owners[i].Owner.OnDeviceLost(this);
                }
            }

            ReleaseBackendDevice();
            if (RecreateBackendDevice().TryGetError(out EngineError error))
            {
                return error;
            }

            (LostOperation, LostNativeCode, LostTimedOut) = (null, 0, false);
            foreach (GpuOwnerRegistration registration in owners)
            {
                if (!_owners.Contains(registration))
                {
                    continue;
                }

                Result recreated = registration.Owner.OnDeviceRecreated(this);
                if (recreated.IsFailure)
                {
                    return recreated;
                }
            }

            return Result.Success;
        }
        finally
        {
            _recovering = false;
        }
    }

    // The registration's side of GpuOwnerRegistration.Dispose: a removed registration isn't found again.
    internal void Unregister(GpuOwnerRegistration registration)
    {
        _owner.ThrowIfNotCurrentThread(nameof(GpuOwnerRegistration) + "." + nameof(GpuOwnerRegistration.Dispose));
        _ = _owners.Remove(registration);
    }

    /// <summary>
    /// The negative canary's seam (section 15): the backend records one known validation violation in the next submitted
    /// frame. Internal: the graphics tests and the conformance suite only.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The device was created without <see cref="GraphicsDeviceOptions.Validation"/> (with no layer to catch it, the
    /// violation would be undefined behaviour), or it is lost; the backend isn't called.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    internal void InjectValidationError()
    {
        ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(InjectValidationError));
        if (!_options.Validation || IsLostCore)
        {
            throw new InvalidOperationException(_options.Validation
                ? "InjectValidationError: the device is lost."
                : "InjectValidationError: the device was created without Validation, so no layer would catch the violation.");
        }

        _backend.InjectValidationError();
    }

    // Raises the event of the loss this call detected, as its last step, so a throwing handler leaves the device as the
    // call documents; passes the call's result through. Allocates nothing.
    private T RaiseDetectedLoss<T>(T result)
    {
        if (_detectedLoss is { } loss)
        {
            _detectedLoss = null;
            DeviceLost?.Invoke(this, loss);
        }

        return result;
    }

    // BeginFrame's last step: if a handler throws, the caller never gets the command list, so the frame isn't left open.
    private FrameStatus RaiseFromBeginFrame(FrameStatus status)
    {
        try
        {
            return RaiseDetectedLoss(status);
        }
        catch
        {
            _frameOpen = false;
            throw;
        }
    }

    // On a lost device a creation or write is refused without a backend call; otherwise the fault seam may replace its
    // backend call with a loss, which marks the device lost as the backend's own would.
    private bool TryRefuse(FaultSeam.Operation operation, string api, out EngineError error)
    {
        if (IsLostCore)
        {
            error = LostError(api);
            return true;
        }

        if (Faults.TryTake(operation, out EngineError loss))
        {
            error = CreationFailed(api, loss);
            return true;
        }

        error = default;
        return false;
    }

    // A creation's or write's backend failure: a loss (Unavailable, or a Timeout the backend remembers from a hung GPU)
    // marks the device lost, raises the event and comes back as Unavailable; anything else comes back unchanged.
    private EngineError CreationFailed(string api, in EngineError error)
    {
        if (error.Kind is not (ErrorKind.Unavailable or ErrorKind.Timeout))
        {
            return error;
        }

        MarkLost(api[(api.LastIndexOf('.') + 1)..], error);
        return RaiseDetectedLoss(AsUnavailable(api, error));
    }

    // A loss as a creation or write reports it: Unavailable, with the loss's native code (a Timeout is rewritten).
    private static EngineError AsUnavailable(string api, in EngineError loss) => loss.Kind == ErrorKind.Unavailable
        ? loss
        : new EngineError(ErrorKind.Unavailable, $"{api}: the device is lost ({loss.Message}).", loss.NativeCode);

    // A call's refusal on a lost device: Unavailable, with the loss's native code. Allocates nothing.
    private EngineError LostError(string api) => new(ErrorKind.Unavailable, LostMessage(api), LostNativeCode);

    // The refusal messages are constants, so a lost device's writes and transient allocations allocate nothing.
    private static string LostMessage(string api) => api switch
    {
        CreateBufferApi => CreateBufferApi + LostSuffix,
        CreateTextureApi => CreateTextureApi + LostSuffix,
        CreateTextureViewApi => CreateTextureViewApi + LostSuffix,
        CreateSamplerApi => CreateSamplerApi + LostSuffix,
        CreateSwapchainApi => CreateSwapchainApi + LostSuffix,
        WriteBufferApi => WriteBufferApi + LostSuffix,
        WriteTextureApi => WriteTextureApi + LostSuffix,
        AllocateTransientApi => AllocateTransientApi + LostSuffix,
        _ => api + LostSuffix, // a call not listed above makes its message
    };

    // After a GPU timeout the backend device is left alone: waiting could hang and destroying could fault. Logged once.
    private void Abandon()
    {
        if (!_backendGone)
        {
            (_pastValidationErrors, _pastValidationWarnings) = (ValidationErrorCount, ValidationWarningCount);
            _backendGone = true;
            _log.Error($"Device abandoned after a GPU timeout: native objects leaked.");
        }
    }

    // Recovery's teardown: every id held for the old backend device is dropped without a backend call (disposing the
    // backend device destroys its objects) and every handle released, then the device is disposed. A retry after a failed
    // re-creation drops what was queued since (destroyed while the backend device was gone).
    private void ReleaseBackendDevice()
    {
        (_pastValidationErrors, _pastValidationWarnings) = (ValidationErrorCount, ValidationWarningCount);
        foreach (FrameSlot slot in _slots)
        {
            DeletionQueue old = slot.Deletions;
            slot.Deletions = new DeletionQueue(_allocator);
            old.Dispose();
            slot.Ring = null;
            slot.PendingBlocks.Clear();
            slot.Uploads.Reset();
        }

        _deferred?.Dispose();
        (_deferred, _pendingUploads, _uploadBytes) = (null, new PendingUploadPool(), 0);
        _retiring.Clear();
        foreach (SwapchainState? state in _swapchains)
        {
            if (state is not null)
            {
                (state.Id, state.Surface, state.Images, state.SurfaceLost, state.NeedsRecreation) = (0, 0, [], false, false);
            }
        }

        _ = _handles.TakeAll();
        if (!_backendGone)
        {
            _backendGone = true;
            _backend.Dispose();
        }
    }

    // Recovery's re-creation, all or nothing: a new backend device on the adapter the options select, which must be this
    // device's adapter with its capabilities, then a new surface for each swapchain.
    private Result RecreateBackendDevice()
    {
        Result<IBackendDevice> opened = OpenBackendDevice(_factory, _request, _options, _log, _objects);
        if (!opened.TryGetValue(out IBackendDevice? device))
        {
            return opened.Error;
        }

        if (device.Adapter.Index != Adapter.Index || device.Adapter.Name != Adapter.Name || !device.Capabilities.Equals(Capabilities))
        {
            device.Dispose();
            return new EngineError(ErrorKind.NotFound, $"Recover: adapter {Adapter.Index} '{Adapter.Name}' is gone or has changed.");
        }

        foreach (SwapchainState? state in _swapchains)
        {
            if (state is null)
            {
                continue;
            }

            Result<ulong> surface = device.CreateSurface(state.Source);
            if (!surface.TryGetValue(out ulong id))
            {
                device.Dispose();
                return surface.Error;
            }

            state.Surface = id;
        }

        (_backend, _backendGone) = (device, false);
        return Result.Success;
    }
}
