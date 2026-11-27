using System;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Internal;

namespace BrutalEngine.Graphics;

/// <content>The frame loop: frame slots, deletion queues and device loss (docs/design/graphics.md sections 1, 4 and 12).</content>
public sealed partial class GraphicsDevice
{
    private const string BeginFrameApi = nameof(GraphicsDevice) + "." + nameof(BeginFrame);
    private const string SubmitFrameApi = nameof(GraphicsDevice) + "." + nameof(SubmitFrame);
    private const string WaitIdleApi = nameof(GraphicsDevice) + "." + nameof(WaitIdle);

    private readonly FrameSlot[] _slots;
    private long _frameNumber;
    private long _completedFrameNumber = -1;
    private bool _frameOpen;

    /// <summary>The frames submitted so far: also the number the next (or open) frame gets.</summary>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Read from a thread other than the owner thread.</exception>
    /// <remarks>Owner thread. Allocates nothing, except the exception.</remarks>
    public long FrameNumber
    {
        get
        {
            ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(FrameNumber));
            return _frameNumber;
        }
    }

    /// <summary>The number of the newest frame whose GPU work has completed; -1 until one has.</summary>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Read from a thread other than the owner thread.</exception>
    /// <remarks>
    /// Frames are numbered from 0 in submission order. It advances when <see cref="BeginFrame"/> has waited for a slot
    /// and when <see cref="WaitIdle"/> succeeds, never otherwise. Owner thread. Allocates nothing, except the exception.
    /// </remarks>
    public long CompletedFrameNumber
    {
        get
        {
            ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(CompletedFrameNumber));
            return _completedFrameNumber;
        }
    }

    /// <summary>Whether the device has been lost: a frame call or <see cref="WaitIdle"/> reported loss or timed out.</summary>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Read from a thread other than the owner thread.</exception>
    /// <remarks>Once lost, the device stays lost. Owner thread. Allocates nothing, except the exception.</remarks>
    public bool IsLost
    {
        get
        {
            ThrowIfUnusable(nameof(GraphicsDevice) + "." + nameof(IsLost));
            return LostOperation is not null;
        }
    }

    /// <summary>The call that detected the loss (<c>"BeginFrame"</c>, ...); <see langword="null"/> while not lost.</summary>
    internal string? LostOperation { get; private set; }

    /// <summary>The native code the backend reported with the loss; 0 while not lost.</summary>
    internal int LostNativeCode { get; private set; }

    /// <summary>Whether the loss was a <see cref="ErrorKind.Timeout"/>, a hung GPU, rather than a reported loss.</summary>
    internal bool LostTimedOut { get; private set; }

    /// <summary>Opens the next frame, after waiting for its slot's previous GPU work.</summary>
    /// <param name="commands">The recorder of the new frame; valid until <see cref="SubmitFrame"/> takes it.</param>
    /// <returns>
    /// <see cref="FrameStatus.Ready"/>, or <see cref="FrameStatus.DeviceLost"/> when the device is lost or this call lost
    /// it. Either way the frame is open and must be submitted.
    /// </returns>
    /// <exception cref="InvalidOperationException">A frame is already open.</exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// The frame uses slot <see cref="FrameNumber"/> modulo the frames in flight. The call waits for the frame last
    /// submitted from that slot (for at most the GPU timeout), advances <see cref="CompletedFrameNumber"/> to it, destroys
    /// the backend objects that slot's frame queued, and opens the slot. A failed wait or open marks the device lost
    /// (a timeout as a hung GPU) and logs it at <see cref="Core.Logging.LogLevel.Error"/>; on a lost device the backend
    /// isn't called. Owner thread. Allocates nothing, except the exception and the log line of a newly detected loss.
    /// </remarks>
    public FrameStatus BeginFrame(out CommandList commands)
    {
        ThrowIfUnusable(BeginFrameApi);
        if (_frameOpen)
        {
            throw new InvalidOperationException($"BeginFrame: frame {_frameNumber} is already open; submit it first.");
        }

        int slotIndex = CurrentSlot();
        FrameSlot slot = _slots[slotIndex];
        _frameOpen = true;
        commands = new CommandList(this, _frameNumber);
        if (IsLostCore || !CheckFrameCall(_backend.WaitForFrame(slotIndex), nameof(BeginFrame)))
        {
            return FrameStatus.DeviceLost;
        }

        _completedFrameNumber = Math.Max(_completedFrameNumber, slot.LastSubmittedFrame);
        slot.Deletions.Retire(_backend);
        slot.Commands.Reset();
        return CheckFrameCall(_backend.BeginFrame(slotIndex), nameof(BeginFrame)) ? FrameStatus.Ready : FrameStatus.DeviceLost;
    }

    /// <summary>Plans the open frame, submits it, and closes it.</summary>
    /// <param name="commands">The open frame's recorder, from this device's last <see cref="BeginFrame"/>.</param>
    /// <returns>
    /// <see cref="FrameStatus.Ready"/>, or <see cref="FrameStatus.DeviceLost"/> when the device is lost or this call lost
    /// it. Either way the frame is closed and <see cref="FrameNumber"/> has grown by one.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// No frame is open, or <paramref name="commands"/> isn't the open frame's: a copy from a submitted frame, another
    /// device's, or <c>default</c>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// The plan holds the frame's recorded commands. A failed submission marks the device lost; on a lost device the
    /// backend isn't called and the frame completes empty. Owner thread. Allocates nothing, except the exception and the
    /// log line of a newly detected loss.
    /// </remarks>
    public FrameStatus SubmitFrame(CommandList commands)
    {
        ThrowIfUnusable(SubmitFrameApi);
        ThrowIfNotOpenFrame(commands, nameof(SubmitFrame));
        int slotIndex = CurrentSlot();
        FrameSlot slot = _slots[slotIndex];
        long frame = _frameNumber++;
        _frameOpen = false;
        slot.LastSubmittedFrame = frame;
        if (IsLostCore)
        {
            return FrameStatus.DeviceLost;
        }

        return CheckFrameCall(_backend.Execute(FramePlanner.Plan(slotIndex, frame, slot.Commands)), nameof(SubmitFrame))
            ? FrameStatus.Ready
            : FrameStatus.DeviceLost;
    }

    /// <summary>Waits until the GPU has finished every submitted frame.</summary>
    /// <returns>
    /// Success, after which <see cref="CompletedFrameNumber"/> is the last submitted frame. Fails with
    /// <see cref="ErrorKind.Unavailable"/> on a lost device, without waiting; a backend failure is returned unchanged, and
    /// an <see cref="ErrorKind.Unavailable"/> or <see cref="ErrorKind.Timeout"/> one also marks the device lost.
    /// </returns>
    /// <exception cref="ObjectDisposedException">The device has been disposed.</exception>
    /// <exception cref="WrongThreadException">Called from a thread other than the owner thread.</exception>
    /// <remarks>
    /// For tests, teardown and recreation; never in steady frames. Destroys the backend objects every submitted frame
    /// queued; an open frame's queue waits for that frame. Owner thread. May allocate.
    /// </remarks>
    public Result WaitIdle()
    {
        ThrowIfUnusable(WaitIdleApi);
        if (IsLostCore)
        {
            return new EngineError(ErrorKind.Unavailable, "WaitIdle: the device is lost.");
        }

        Result result = _backend.WaitIdle();
        if (result.TryGetError(out EngineError error))
        {
            if (error.Kind is ErrorKind.Unavailable or ErrorKind.Timeout)
            {
                MarkLost(nameof(WaitIdle), error);
            }

            return result;
        }

        _completedFrameNumber = _frameNumber - 1;
        for (int i = 0; i < _slots.Length; i++)
        {
            if (!_frameOpen || i != CurrentSlot())
            {
                _slots[i].Deletions.Retire(_backend);
            }
        }

        return result;
    }

    private bool IsLostCore => LostOperation is not null;

    private static FrameSlot[] CreateSlots(int count, INativeAllocator allocator)
    {
        var slots = new FrameSlot[count];
        try
        {
            for (int i = 0; i < count; i++)
            {
                slots[i] = new FrameSlot(allocator);
            }
        }
        catch
        {
            DisposeSlots(slots);
            throw;
        }

        return slots;
    }

    private static void DisposeSlots(FrameSlot?[] slots)
    {
        foreach (FrameSlot? slot in slots)
        {
            slot?.Dispose();
        }
    }

    private int CurrentSlot() => (int)(_frameNumber % _slots.Length);

    // The deletion queue a destroyed object waits on, with room reserved for one entry: the open frame's, else the last
    // submitted frame's; null before the first submission, when nothing can still use the object.
    private DeletionQueue? ReserveDeletion()
    {
        if (!_frameOpen && _frameNumber == 0)
        {
            return null;
        }

        DeletionQueue queue = _slots[(int)((_frameOpen ? _frameNumber : _frameNumber - 1) % _slots.Length)].Deletions;
        queue.Reserve();
        return queue;
    }

    private void ThrowIfNotOpenFrame(CommandList commands, string api)
    {
        if (!_frameOpen || commands.Device != this || commands.FrameNumber != _frameNumber)
        {
            throw new InvalidOperationException(
                $"{api}: the command list isn't the open frame's (open frame: {(_frameOpen ? _frameNumber : -1)}). Use the one the last BeginFrame returned, once.");
        }
    }

    // A frame call has no partial recovery: any failure is device loss (the backend contract's frame-call rule).
    private bool CheckFrameCall(Result result, string operation)
    {
        if (result.TryGetError(out EngineError error))
        {
            MarkLost(operation, error);
            return false;
        }

        return true;
    }

    private void MarkLost(string operation, in EngineError error)
    {
        // TODO(#115): raise DeviceLost once here.
        LostOperation = operation;
        LostNativeCode = error.NativeCode;
        LostTimedOut = error.Kind == ErrorKind.Timeout;
        _log.Error($"Device lost in {operation}: {error.Kind} (native code {error.NativeCode}): {error.Message}");
    }
}
