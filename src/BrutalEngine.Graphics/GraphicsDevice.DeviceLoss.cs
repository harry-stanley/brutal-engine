using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Internal;

namespace BrutalEngine.Graphics;

/// <content>Device loss: the fault seam and the DeviceLost event (docs/design/graphics.md section 12).</content>
public sealed partial class GraphicsDevice
{
    private const string LostSuffix = ": the device is lost.";

    private DeviceLostEventArgs? _detectedLoss;

    /// <summary>
    /// Raised once when the device is lost, synchronously, on the owner thread, as the last step of the call that
    /// detected the loss.
    /// </summary>
    /// <remarks>
    /// That call is <see cref="BeginFrame"/>, <see cref="SubmitFrame"/>, <see cref="CommandList.AcquireSwapchainImage"/>,
    /// <see cref="WaitIdle"/>, or a creation or write whose backend call reported loss, as
    /// <see cref="DeviceLostEventArgs.Operation"/> names it. It has done its work when the handlers run, and
    /// <see cref="IsLost"/> is <see langword="true"/>. A handler's exception propagates out of that call, and the event
    /// isn't raised again for the loss. Adding or removing a handler is owner thread only and isn't checked.
    /// </remarks>
    public event EventHandler<DeviceLostEventArgs>? DeviceLost;

    /// <summary>The fault seam (section 12), for the graphics tests and the conformance suite only.</summary>
    internal FaultSeam Faults { get; } = new();

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

    // A call's refusal on a lost device: Unavailable, with the loss's native code.
    private EngineError LostError(string api) => new(ErrorKind.Unavailable, api + LostSuffix, LostNativeCode);
}
