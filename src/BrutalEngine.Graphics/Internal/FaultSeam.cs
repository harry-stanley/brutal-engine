using System;
using BrutalEngine.Core;

namespace BrutalEngine.Graphics.Internal;

/// <summary>
/// The device's fault-injection seam (docs/design/graphics.md section 12): a test arms a loss for the N-th call of one
/// backend operation, and that call then reports the loss instead of reaching the backend.
/// </summary>
/// <remarks>
/// Every backend call whose status the device acts on asks <see cref="TryTake"/> first, so a loss can be forced at any of
/// them on any backend. Visible to the graphics tests and the conformance suite only. One loss is armed at a time.
/// Not thread-safe: the device and the tests use it on the owner thread only.
/// </remarks>
internal sealed class FaultSeam
{
    private Operation _operation;
    private int _remaining;
    private EngineError _loss;

    /// <summary>
    /// The backend calls the seam can make report a loss, named after their <see cref="Backend.IBackendDevice"/> members:
    /// the frame calls, <see cref="Present"/> and <see cref="AcquireImage"/> (which report
    /// <see cref="Backend.SwapchainStatus.DeviceLost"/>), the idle wait (a swapchain recreation's too), and every creation:
    /// <see cref="CreateBuffer"/> covers staging blocks, <see cref="CreateTextureView"/> default views, and
    /// <see cref="CreateSwapchain"/> recreations.
    /// </summary>
    internal enum Operation
    {
        WaitForFrame = 0,
        BeginFrame = 1,
        Execute = 2,
        Present = 3,
        AcquireImage = 4,
        WaitIdle = 5,
        CreateBuffer = 6,
        CreateTexture = 7,
        CreateTextureView = 8,
        CreateSampler = 9,
        CreateSurface = 10,
        CreateSwapchain = 11,
    }

    /// <summary>Arms a loss: the <paramref name="occurrence"/>-th call of <paramref name="operation"/> from now reports it.</summary>
    /// <param name="operation">The backend call to replace.</param>
    /// <param name="occurrence">Which call: 1 for the next one, 2 for the one after, and so on.</param>
    /// <param name="loss">
    /// The status the call reports: <see cref="ErrorKind.Unavailable"/> (a reported loss) or <see cref="ErrorKind.Timeout"/>
    /// (an expired GPU timeout), with its native code. <see cref="Operation.Present"/> and <see cref="Operation.AcquireImage"/>
    /// report <see cref="Backend.SwapchainStatus.DeviceLost"/> with the native code, so they take only
    /// <see cref="ErrorKind.Unavailable"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="operation"/> isn't an <see cref="Operation"/> member, or <paramref name="occurrence"/> is below 1.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="loss"/> isn't a loss: its kind is neither <see cref="ErrorKind.Unavailable"/> nor
    /// <see cref="ErrorKind.Timeout"/>, or it is a timeout for an operation that reports a swapchain status.
    /// </exception>
    /// <remarks>Replaces a loss armed earlier and not yet taken. Allocates nothing, except the exception.</remarks>
    public void InjectLoss(Operation operation, int occurrence, in EngineError loss)
    {
        if ((uint)operation > (uint)Operation.CreateSwapchain)
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "InjectLoss: not a FaultSeam.Operation member.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(occurrence, 1);
        bool reportsStatus = operation is Operation.Present or Operation.AcquireImage;
        if (loss.Kind is not (ErrorKind.Unavailable or ErrorKind.Timeout) || (reportsStatus && loss.Kind == ErrorKind.Timeout))
        {
            throw new ArgumentException(
                $"InjectLoss: {loss.Kind} isn't a loss {operation} can report (Unavailable, or Timeout for a call that returns a Result).", nameof(loss));
        }

        (_operation, _remaining, _loss) = (operation, occurrence, loss);
    }

    /// <summary>Counts a call of <paramref name="operation"/>, and takes the armed loss when this is its call.</summary>
    /// <param name="operation">The backend call about to be made.</param>
    /// <param name="loss">The armed loss when the result is <see langword="true"/>; <c>default</c> otherwise.</param>
    /// <returns>
    /// <see langword="true"/> when this call is the armed one: the caller reports <paramref name="loss"/> instead of calling
    /// the backend, and the seam is disarmed. <see langword="false"/> otherwise.
    /// </returns>
    /// <remarks>Only calls of the armed operation count toward the occurrence. Allocates nothing.</remarks>
    public bool TryTake(Operation operation, out EngineError loss)
    {
        if (_remaining == 0 || operation != _operation || --_remaining > 0)
        {
            loss = default;
            return false;
        }

        loss = _loss;
        return true;
    }
}
