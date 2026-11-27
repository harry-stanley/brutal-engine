using System;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Tests.Device;
using BrutalEngine.TestUtilities;
using Xunit;
using Op = BrutalEngine.TestUtilities.FakeBackendOperation;

namespace BrutalEngine.Graphics.Tests.Frames;

/// <summary>Frame slots, frame-order misuse and device loss in frame calls (docs/design/graphics.md sections 4 and 12).</summary>
public sealed class FrameLifecycleTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void NewDevice_NoFramesAndNotLost()
    {
        GraphicsDevice device = _fixture.Create();

        Assert.Equal((0L, -1L, false, 0L), (device.FrameNumber, device.CompletedFrameNumber, device.IsLost, device.Statistics.FramesSubmitted));
        Assert.Equal((null, 0, false), (device.LostOperation, device.LostNativeCode, device.LostTimedOut));
    }

    [Fact]
    public void Slots_CycleAndWait()
    {
        _fixture.Script.RecordPlans = true;
        GraphicsDevice device = _fixture.Create(_fixture.Options() with { FramesInFlight = 3 });
        FakeBackendDevice backend = _fixture.BackendDevice;

        for (long frame = 0; frame < 8; frame++)
        {
            int slot = (int)(frame % 3);
            // From the fourth frame on, the slot's previous frame is still running on the "GPU" when BeginFrame starts.
            Assert.Equal(frame < 3, backend.IsFrameComplete(slot).Value);
            int start = backend.Calls.Count;

            Assert.Equal(FrameStatus.Ready, device.BeginFrame(out CommandList commands));

            // The slot was waited for before it was opened, and the wait completed the slot's previous frame.
            Assert.Equal([Call(Op.WaitForFrame, slot), Call(Op.BeginFrame, slot)], CallsSince(start));
            Assert.True(backend.IsFrameComplete(slot).Value);
            Assert.Equal(frame < 3 ? -1 : frame - 3, device.CompletedFrameNumber);
            Assert.Equal(frame, device.FrameNumber);

            start = backend.Calls.Count;
            Assert.Equal(FrameStatus.Ready, device.SubmitFrame(commands));

            Assert.Equal([Call(Op.Execute, slot)], CallsSince(start));
            RecordedPlan plan = backend.Plans[^1];
            Assert.Equal((slot, frame, 0, 0), (plan.FrameSlot, plan.FrameNumber, plan.Commands.Length, plan.Acquired.Length));
            Assert.Equal(frame + 1, device.FrameNumber);
            Assert.Equal(frame + 1, device.Statistics.FramesSubmitted);
        }
    }

    [Fact]
    public void CompletedFrameNumber_AdvancesOnlyWhenObserved()
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        RunFrame(device);
        RunFrame(device);

        // The GPU finished both frames, but the device hasn't looked yet.
        backend.CompleteFrame(0);
        backend.CompleteFrame(1);
        Assert.Equal(-1, device.CompletedFrameNumber);

        // Frame 2 waits for slot 0, whose last frame was 0; slot 1 isn't polled.
        _ = device.BeginFrame(out CommandList commands);
        Assert.Equal(0, device.CompletedFrameNumber);
        _ = device.SubmitFrame(commands);

        Assert.True(device.WaitIdle().IsSuccess);
        Assert.Equal(2, device.CompletedFrameNumber);

        // Frame 3 waits for slot 1, whose frame (1) is older than what WaitIdle saw: the number never goes back.
        _ = device.BeginFrame(out commands);
        Assert.Equal(2, device.CompletedFrameNumber);
        _ = device.SubmitFrame(commands);
    }

    public static TheoryData<string> MisuseCases() =>
    [
        "BeginFrame while open", "SubmitFrame with no frame open", "SubmitFrame default", "SubmitFrame copy after its frame",
        "SubmitFrame copy in the next frame", "SubmitFrame another device's",
    ];

    [Theory]
    [MemberData(nameof(MisuseCases))]
    public void Misuse_Throws(string misuse)
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        GraphicsDevice other = _fixture.Create();
        FakeBackendDevice otherBackend = _fixture.BackendDevice;
        RunFrame(device);
        _ = device.BeginFrame(out CommandList stale);
        _ = device.SubmitFrame(stale);
        bool open = misuse != "SubmitFrame with no frame open" && misuse != "SubmitFrame copy after its frame";
        CommandList commands = default;
        if (open)
        {
            _ = device.BeginFrame(out commands);
        }

        // The other device's frame 2 has the same number as this device's open frame: only the device tells them apart.
        RunFrame(other);
        RunFrame(other);
        _ = other.BeginFrame(out CommandList foreign);
        (int calls, int otherCalls) = (backend.Calls.Count, otherBackend.Calls.Count);
        long frameNumber = device.FrameNumber;

        InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => _ = misuse switch
        {
            "BeginFrame while open" => device.BeginFrame(out _),
            "SubmitFrame another device's" => device.SubmitFrame(foreign),
            "SubmitFrame default" or "SubmitFrame with no frame open" => device.SubmitFrame(default),
            _ => device.SubmitFrame(stale),
        });

        Assert.StartsWith(misuse.Split(' ')[0] + ":", thrown.Message, StringComparison.Ordinal);
        // Nothing happened: no backend call on either device, the same frame, and an open frame still submits.
        Assert.Equal((calls, otherCalls), (backend.Calls.Count, otherBackend.Calls.Count));
        Assert.Equal(frameNumber, device.FrameNumber);
        if (open)
        {
            Assert.Equal(FrameStatus.Ready, device.SubmitFrame(commands));
        }

        Assert.Equal(FrameStatus.Ready, other.SubmitFrame(foreign));
    }

    public static TheoryData<Op, ErrorKind, int, bool> FrameCallFailures() => new()
    {
        { Op.WaitForFrame, ErrorKind.Unavailable, -4, false },
        { Op.WaitForFrame, ErrorKind.Timeout, 2, true },
        { Op.BeginFrame, ErrorKind.Native, -13, false },
        { Op.Execute, ErrorKind.OutOfMemory, -2, false },
        { Op.Execute, ErrorKind.Unavailable, -4, false },
    };

    [Theory]
    [MemberData(nameof(FrameCallFailures))]
    public void FailedFrameCall_IsLossAndFrameCompletes(Op operation, ErrorKind kind, int nativeCode, bool timedOut)
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        RunFrame(device);
        _fixture.Script.FailNext(operation, new EngineError(kind, "Injected frame-call failure.", nativeCode));
        bool atBegin = operation != Op.Execute;

        FrameStatus begun = device.BeginFrame(out CommandList commands);
        FrameStatus submitted = device.SubmitFrame(commands);

        Assert.Equal(atBegin ? FrameStatus.DeviceLost : FrameStatus.Ready, begun);
        Assert.Equal(FrameStatus.DeviceLost, submitted);
        // The frame still completed: it was submitted (counted) and closed, but a frame lost at its start never executed.
        Assert.Equal(2, device.FrameNumber);
        Assert.Equal(atBegin ? 1 : 2, backend.Calls.Count(call => call.Operation == Op.Execute));
        Assert.True(device.IsLost);
        Assert.Equal((atBegin ? "BeginFrame" : "SubmitFrame", nativeCode, timedOut), (device.LostOperation, device.LostNativeCode, device.LostTimedOut));
        Assert.Contains(_fixture.Entries(), entry => entry.Level == LogLevel.Error && entry.Message.StartsWith("Device lost in ", StringComparison.Ordinal)
            && entry.Message.Contains(kind.ToString(), StringComparison.Ordinal));

        // Later frames still begin and submit, empty, without calling the backend.
        int calls = backend.Calls.Count;
        Assert.Equal(FrameStatus.DeviceLost, device.BeginFrame(out commands));
        Assert.Equal(FrameStatus.DeviceLost, device.SubmitFrame(commands));
        Assert.Equal(calls, backend.Calls.Count);
        Assert.Equal(3, device.FrameNumber);
        Assert.Equal(3, device.Statistics.FramesSubmitted);
        Assert.Equal(-1, device.CompletedFrameNumber);
    }

    [Fact]
    public void LossAfterWait_KeepsCompletedFrameNumber()
    {
        GraphicsDevice device = _fixture.Create(_fixture.Options() with { FramesInFlight = 1 });
        RunFrame(device);
        _fixture.Script.FailNext(Op.BeginFrame, new EngineError(ErrorKind.Native, "Injected pool reset failure.", -1));

        Assert.Equal(FrameStatus.DeviceLost, device.BeginFrame(out CommandList commands));

        // The wait succeeded before the open failed, so frame 0 is known to be complete.
        Assert.Equal(0, device.CompletedFrameNumber);
        Assert.Equal(FrameStatus.DeviceLost, device.SubmitFrame(commands));
    }

    [Fact]
    public void WaitIdle_CompletesSubmittedFramesAndKeepsOpenFrame()
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        SamplerHandle first = device.CreateSampler(new SamplerDescription()).Value;
        SamplerHandle second = device.CreateSampler(new SamplerDescription()).Value;
        ulong firstId = backend.Objects.GetId(first);
        ulong secondId = backend.Objects.GetId(second);
        RunFrame(device);
        device.Destroy(first);
        _ = device.BeginFrame(out CommandList commands);
        device.Destroy(second);
        int start = backend.Calls.Count;

        Assert.True(device.WaitIdle().IsSuccess);

        // Frame 0 is complete, so its queue is retired; the open frame 1 may still use the second sampler.
        Assert.Equal([Call(Op.WaitIdle), new FakeBackendCall(Op.Destroy, BackendObjectKind.Sampler, firstId, -1)], CallsSince(start));
        Assert.Equal(0, device.CompletedFrameNumber);

        _ = device.SubmitFrame(commands);
        start = backend.Calls.Count;
        Assert.True(device.WaitIdle().IsSuccess);

        Assert.Equal([Call(Op.WaitIdle), new FakeBackendCall(Op.Destroy, BackendObjectKind.Sampler, secondId, -1)], CallsSince(start));
        Assert.Equal(1, device.CompletedFrameNumber);
    }

    [Fact]
    public void WaitIdle_BeforeAnyFrame_Succeeds()
    {
        GraphicsDevice device = _fixture.Create();

        Assert.True(device.WaitIdle().IsSuccess);

        Assert.Equal(-1, device.CompletedFrameNumber);
        Assert.Equal(Op.WaitIdle, _fixture.BackendDevice.Calls[^1].Operation);
    }

    [Fact]
    public void WaitIdle_LostDevice_FailsWithoutBackend()
    {
        GraphicsDevice device = _fixture.Create();
        RunFrame(device);
        _fixture.Script.FailNext(Op.Execute, new EngineError(ErrorKind.Unavailable, "Injected loss.", -4));
        RunFrame(device);
        int calls = _fixture.BackendDevice.Calls.Count;

        Result result = device.WaitIdle();

        Assert.Equal(ErrorKind.Unavailable, result.Error.Kind);
        Assert.Equal("WaitIdle: the device is lost.", result.Error.Message);
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
        Assert.Equal("SubmitFrame", device.LostOperation);
    }

    public static TheoryData<ErrorKind, bool> WaitIdleFailures() => new()
    {
        { ErrorKind.Unavailable, true },
        { ErrorKind.Timeout, true },
        { ErrorKind.Native, false },
        { ErrorKind.OutOfMemory, false },
    };

    [Theory]
    [MemberData(nameof(WaitIdleFailures))]
    public void WaitIdle_BackendFailure_ReturnedAndLossOnlyForLoss(ErrorKind kind, bool lost)
    {
        GraphicsDevice device = _fixture.Create();
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        RunFrame(device);
        device.Destroy(sampler);
        var error = new EngineError(kind, "Injected WaitIdle failure.", 7);
        _fixture.Script.FailNext(Op.WaitIdle, error);
        int calls = _fixture.BackendDevice.Calls.Count;

        Result result = device.WaitIdle();

        Assert.Equal(error, result.Error);
        Assert.Equal(lost, device.IsLost);
        Assert.Equal(lost ? ("WaitIdle", 7, kind == ErrorKind.Timeout) : (null, 0, false), (device.LostOperation, device.LostNativeCode, device.LostTimedOut));
        // Nothing is known to be complete, so the queued sampler wasn't destroyed.
        Assert.Equal(-1, device.CompletedFrameNumber);
        Assert.Equal([Call(Op.WaitIdle)], CallsSince(calls));
    }

    [Fact]
    public void Dispose_AfterFrames_WaitsThenDestroysQueuedObjects()
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        ulong id = backend.Objects.GetId(sampler);
        RunFrame(device);
        device.Destroy(sampler);
        int start = backend.Calls.Count;

        device.Dispose();

        Assert.Equal(
            [Call(Op.WaitIdle), new FakeBackendCall(Op.Destroy, BackendObjectKind.Sampler, id, -1), Call(Op.Dispose)],
            CallsSince(start));
    }

    [Fact]
    public void Dispose_NoFrameSubmitted_DoesNotWait()
    {
        GraphicsDevice device = _fixture.Create();
        _ = device.BeginFrame(out _);
        int start = _fixture.BackendDevice.Calls.Count;

        device.Dispose();

        Assert.Equal([Call(Op.Dispose)], CallsSince(start));
    }

    [Fact]
    public void Dispose_WaitFails_LogsAndStillDestroys()
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        ulong id = backend.Objects.GetId(sampler);
        RunFrame(device);
        device.Destroy(sampler);
        _fixture.Script.FailNext(Op.WaitIdle, new EngineError(ErrorKind.Timeout, "Injected hang.", 2));

        device.Dispose();

        Assert.Equal([Op.WaitIdle, Op.Destroy, Op.Dispose], backend.Calls.TakeLast(3).Select(call => call.Operation));
        Assert.Equal(id, backend.Calls[^2].Id);
        Assert.Contains(_fixture.Entries(), entry => entry.Level == LogLevel.Error
            && entry.Message.StartsWith("Dispose: waiting for the GPU failed (Timeout, native code 2: Injected hang.)", StringComparison.Ordinal));
    }

    [Fact]
    public void Dispose_LostDevice_DestroysQueuedObjectsWithoutWaiting()
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        _fixture.Script.FailNext(Op.Execute, new EngineError(ErrorKind.Unavailable, "Injected loss.", -4));
        _ = device.BeginFrame(out CommandList commands);
        _ = device.SubmitFrame(commands);
        // On a lost device the object still waits on the queue (recovery, later, discards it instead).
        device.Destroy(sampler);
        Assert.DoesNotContain(backend.Calls, call => call.Operation == Op.Destroy);
        int start = backend.Calls.Count;

        device.Dispose();

        Assert.Equal([Op.Destroy, Op.Dispose], CallsSince(start).Select(call => call.Operation));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Create_SlotMemoryFails_DisposesBackendDeviceAndFreesEverything(int successes)
    {
        // Two slots of two blocks each (command stream, deletion queue): each row fails a different one.
        var failing = new FailingAllocator(_fixture.Allocator) { FailAfter = successes };

        Assert.ThrowsAny<OutOfMemoryException>(() => GraphicsDevice.Create(_fixture.Backend, _fixture.Options() with { Allocator = failing }));

        Assert.Equal(successes, failing.Allocations);
        Assert.True(_fixture.BackendDevice.IsDisposed);
        // The fixture's Dispose checks the tracking allocator: every block allocated before the failure was freed.
        Assert.Equal(0, _fixture.Allocator.LiveAllocationCount);
    }

    private static FakeBackendCall Call(Op operation, int frameSlot = -1) => new(operation, null, 0, frameSlot);

    private static void RunFrame(GraphicsDevice device)
    {
        _ = device.BeginFrame(out CommandList commands);
        _ = device.SubmitFrame(commands);
    }

    private FakeBackendCall[] CallsSince(int start) => [.. _fixture.BackendDevice.Calls.Skip(start)];
}
