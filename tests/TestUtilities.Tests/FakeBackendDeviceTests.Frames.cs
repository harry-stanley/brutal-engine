using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics;
using BrutalEngine.Graphics.Backend;
using Xunit;

namespace BrutalEngine.TestUtilities.Tests;

/// <summary>The fake backend device's frames: slot order, completion, presents, timestamps and plan recording.</summary>
public sealed partial class FakeBackendDeviceTests
{
    private static readonly string[] _frameViolations =
    [
        "WaitForFrame slot below range",
        "WaitForFrame slot above range",
        "WaitForFrame on an open slot",
        "IsFrameComplete slot above range",
        "BeginFrame before WaitForFrame",
        "BeginFrame twice",
        "BeginFrame after a failed WaitForFrame",
        "AcquireImage outside an open frame",
        "Execute outside an open frame",
        "Execute a slot twice",
        "Execute waiting for an image not acquired",
        "Execute without an image it acquired",
        "Execute waiting for another image of the swapchain",
        "Execute waiting for another swapchain's image",
        "Execute listing an acquired image twice",
        "Present an image not executed",
        "Present an image twice",
        "Present an image after its Execute failed",
        "Present an earlier frame's image after a failed Execute",
        "TryReadTimestamps slot above range",
        "WaitForFrame after Dispose",
    ];

    [Fact]
    public void Frames_FollowSlotOrder()
    {
        FakeBackendDevice device = _fixture.CreateDevice();

        // A slot that never submitted is complete, and its wait returns at once.
        Assert.True(device.IsFrameComplete(1).Value);
        FakeBackendFixture.OpenFrame(device, 0);
        Assert.True(device.Execute(Plan(0, 7)).IsSuccess);

        // Executed: pending until the GPU "finishes" it; the other slot is unaffected.
        Assert.False(device.IsFrameComplete(0).Value);
        Assert.True(device.IsFrameComplete(1).Value);
        device.CompleteFrame(0);
        Assert.True(device.IsFrameComplete(0).Value);

        // A wait completes a pending frame, and an Execute that fails before submitting leaves the slot complete.
        FakeBackendFixture.OpenFrame(device, 0);
        Assert.True(device.Execute(Plan(0, 9)).IsSuccess);
        FakeBackendFixture.OpenFrame(device, 0);
        Assert.True(device.IsFrameComplete(0).Value);
        _fixture.Script.FailNext(FakeBackendOperation.Execute, new EngineError(ErrorKind.OutOfMemory, "submit"));
        Assert.True(device.Execute(Plan(0, 11)).IsFailure);
        Assert.True(device.IsFrameComplete(0).Value);

        // WaitIdle completes every slot.
        FakeBackendFixture.OpenFrame(device, 1);
        Assert.True(device.Execute(Plan(1, 12)).IsSuccess);
        Assert.True(device.WaitIdle().IsSuccess);
        Assert.True(device.IsFrameComplete(1).Value);
        Assert.Equal(new FakeBackendCall(FakeBackendOperation.Execute, null, 0, 1), device.Calls[^3]);
    }

    [Fact]
    public void Frames_ScriptedFailures_ReturnedOnce()
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        var timeout = new EngineError(ErrorKind.Timeout, "hung", nativeCode: 2);
        var lost = new EngineError(ErrorKind.Unavailable, "lost", nativeCode: -4);

        _fixture.Script.FailNext(FakeBackendOperation.WaitForFrame, timeout);
        Assert.Equal(timeout, device.WaitForFrame(0).Error);
        Assert.Throws<InvalidOperationException>(() => device.BeginFrame(0));
        _fixture.Script.FailNext(FakeBackendOperation.BeginFrame, lost);
        Assert.True(device.WaitForFrame(0).IsSuccess);
        Assert.Equal(lost, device.BeginFrame(0).Error);
        _fixture.Script.FailNext(FakeBackendOperation.IsFrameComplete, lost);
        Assert.Equal(lost, device.IsFrameComplete(0).Error);
        Assert.True(device.IsFrameComplete(0).Value);
        FakeBackendFixture.OpenFrame(device, 0);
    }

    [Fact]
    public void Present_FollowsTheLastExecute()
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        (_, _, ulong swapchain) = Presentable(device);
        FakeBackendFixture.OpenFrame(device, 0);
        int unpresented = FakeBackendFixture.ExecuteWithImage(device, 0, swapchain);

        // The next frame's Execute replaces what is presentable.
        FakeBackendFixture.OpenFrame(device, 1);
        int image = FakeBackendFixture.ExecuteWithImage(device, 1, swapchain);

        Assert.Throws<InvalidOperationException>(() => device.Present(swapchain, unpresented));
        Assert.Equal(SwapchainStatus.Success, device.Present(swapchain, image).Status);
    }

    [Fact]
    public void RecordPlans_CopiesBytesAndAcquired()
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        (_, _, ulong swapchain) = Presentable(device);
        byte[] commands = [1, 2, 3, 4];
        FakeBackendFixture.OpenFrame(device, 1);
        int image = device.AcquireImage(swapchain, 1).ImageIndex;
        SwapchainImageUse[] acquired = [new(swapchain, image)];

        Assert.True(device.Execute(GraphicsInternals.NewFramePlan(1, 5, commands, acquired)).IsSuccess);
        _fixture.Script.RecordPlans = true;
        FakeBackendFixture.OpenFrame(device, 1);
        Assert.True(device.Execute(GraphicsInternals.NewFramePlan(1, 6, commands, [])).IsSuccess);
        FakeBackendFixture.OpenFrame(device, 0);
        image = device.AcquireImage(swapchain, 0).ImageIndex;
        acquired[0] = new SwapchainImageUse(swapchain, image);
        Assert.True(device.Execute(GraphicsInternals.NewFramePlan(0, 7, commands, acquired)).IsSuccess);
        commands.AsSpan().Clear();
        acquired[0] = default;

        Assert.Equal(2, device.Plans.Count);
        RecordedPlan first = device.Plans[0];
        RecordedPlan second = device.Plans[1];
        Assert.Equal((1, 6L, 0), (first.FrameSlot, first.FrameNumber, first.Acquired.Length));
        Assert.Equal((0, 7L), (second.FrameSlot, second.FrameNumber));
        Assert.Equal([1, 2, 3, 4], second.Commands.ToArray());
        Assert.Equal([new SwapchainImageUse(swapchain, image)], second.Acquired.ToArray());
    }

    [Fact]
    public void RecordCalls_Off_RecordsNothing()
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        _fixture.Script.RecordCalls = false;

        FakeBackendFixture.OpenFrame(device, 0);
        Assert.True(device.Execute(Plan(0, 1)).IsSuccess);
        _ = device.CreateSampler(new SamplerDescription()).Value;

        Assert.Empty(device.Calls);
    }

    [Fact]
    public void TryReadTimestamps_AfterCompletion_ReportsScriptedTicks()
    {
        _fixture.Script.Capabilities = FakeBackendScript.DefaultCapabilities with { Features = GraphicsFeatures.TimestampQueries };
        _fixture.Script.TimestampTicks = (frame, query) => (ulong)((frame * 100) + query);
        FakeBackendDevice device = _fixture.CreateDevice();
        Span<ulong> ticks = stackalloc ulong[3];

        Assert.False(device.TryReadTimestamps(0, ticks));
        FakeBackendFixture.OpenFrame(device, 0);
        Assert.True(device.Execute(Plan(0, 4)).IsSuccess);
        Assert.False(device.TryReadTimestamps(0, ticks));
        device.CompleteFrame(0);

        Assert.True(device.TryReadTimestamps(0, ticks));
        Assert.Equal([400UL, 401, 402], ticks.ToArray());
    }

    [Fact]
    public void TryReadTimestamps_AfterTheSlotBeginsAgain_ReturnsFalse()
    {
        _fixture.Script.Capabilities = FakeBackendScript.DefaultCapabilities with { Features = GraphicsFeatures.TimestampQueries };
        FakeBackendDevice device = _fixture.CreateDevice();
        Span<ulong> ticks = stackalloc ulong[2];
        FakeBackendFixture.OpenFrame(device, 0);
        Assert.True(device.Execute(Plan(0, 4)).IsSuccess);

        // The wait leaves the completed frame's ticks readable; BeginFrame resets the slot's queries.
        Assert.True(device.WaitForFrame(0).IsSuccess);
        Assert.True(device.TryReadTimestamps(0, ticks));
        Assert.True(device.BeginFrame(0).IsSuccess);

        Assert.False(device.TryReadTimestamps(0, ticks));
    }

    [Fact]
    public void TryReadTimestamps_AfterAFailedExecute_ReturnsFalse()
    {
        _fixture.Script.Capabilities = FakeBackendScript.DefaultCapabilities with { Features = GraphicsFeatures.TimestampQueries };
        _fixture.Script.TimestampTicks = (frame, query) => (ulong)((frame * 100) + query);
        FakeBackendDevice device = _fixture.CreateDevice();
        Span<ulong> ticks = stackalloc ulong[2];
        FakeBackendFixture.OpenFrame(device, 0);
        Assert.True(device.Execute(Plan(0, 4)).IsSuccess);
        FakeBackendFixture.OpenFrame(device, 0);
        _fixture.Script.FailNext(FakeBackendOperation.Execute, new EngineError(ErrorKind.Unavailable, "lost"));

        Assert.True(device.Execute(Plan(0, 5)).IsFailure);
        Assert.True(device.WaitForFrame(0).IsSuccess);
        Assert.False(device.TryReadTimestamps(0, ticks));

        // The slot's next submitted frame reports its own ticks.
        Assert.True(device.BeginFrame(0).IsSuccess);
        Assert.True(device.Execute(Plan(0, 6)).IsSuccess);
        device.CompleteFrame(0);
        Assert.True(device.TryReadTimestamps(0, ticks));
        Assert.Equal([600UL, 601], ticks.ToArray());
    }

    [Fact]
    public void TryReadTimestamps_WithoutTheFeature_ReturnsFalse()
    {
        FakeBackendDevice device = _fixture.CreateDevice();
        FakeBackendFixture.OpenFrame(device, 0);
        Assert.True(device.Execute(Plan(0, 1)).IsSuccess);
        device.CompleteFrame(0);

        Assert.False(device.TryReadTimestamps(0, stackalloc ulong[2]));
    }

    [Fact]
    public void InjectValidationError_NextExecuteReportsOneError()
    {
        FakeBackendDevice device = _fixture.CreateDevice(validation: true);
        device.InjectValidationError();

        FakeBackendFixture.OpenFrame(device, 0);
        Assert.True(device.Execute(Plan(0, 1)).IsSuccess);
        FakeBackendFixture.OpenFrame(device, 0);
        Assert.True(device.Execute(Plan(0, 2)).IsSuccess);

        Assert.Equal(1, device.ValidationErrors);
    }

    [Fact]
    public void CompleteFrame_SlotOutOfRange_Throws()
    {
        FakeBackendDevice device = _fixture.CreateDevice();

        Assert.Throws<ArgumentOutOfRangeException>(() => device.CompleteFrame(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => device.CompleteFrame(2));
    }

    [Fact]
    public void SteadyFrameMembers_DoNotAllocate()
    {
        _fixture.Script.RecordCalls = false;
        _fixture.Script.RecordPlans = false;
        FakeBackendDevice device = _fixture.CreateDevice();
        (_, _, ulong swapchain) = Presentable(device);
        byte[] commands = new byte[64];
        var acquired = new SwapchainImageUse[1];
        RunFrames(device, swapchain, commands, acquired, first: 0, count: 8);

        // Enough frames that a recorded-call list would cross at least one capacity doubling.
        long before = GC.GetAllocatedBytesForCurrentThread();
        RunFrames(device, swapchain, commands, acquired, first: 8, count: 512);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Empty(device.Calls);
    }

    private static void RunFrames(FakeBackendDevice device, ulong swapchain, byte[] commands, SwapchainImageUse[] acquired, int first, int count)
    {
        for (int frame = first; frame < first + count; frame++)
        {
            int slot = frame % 2;
            bool ok = device.WaitForFrame(slot).IsSuccess && device.IsFrameComplete(slot).Value && device.BeginFrame(slot).IsSuccess;
            BackendAcquire acquire = device.AcquireImage(swapchain, slot);
            acquired[0] = new SwapchainImageUse(swapchain, acquire.ImageIndex);
            ok &= device.Execute(GraphicsInternals.NewFramePlan(slot, frame, commands, acquired)).IsSuccess;
            ok &= device.Present(swapchain, acquire.ImageIndex).Status == SwapchainStatus.Success;
            device.CompleteFrame(slot);
            if (!ok)
            {
                // Asserting inside the measured loop would allocate; fail outside the hot path instead.
                throw new InvalidOperationException($"Frame {frame} didn't run.");
            }
        }

    }

    private static FramePlan Plan(int slot, long frame) => GraphicsInternals.NewFramePlan(slot, frame, [], []);

    private Action FrameViolation(FakeBackendDevice device, string violation)
    {
        (_, _, ulong swapchain) = Presentable(device);
        int image = 0;
        ulong second = 0;
        return violation switch
        {
            "WaitForFrame slot below range" => () => device.WaitForFrame(-1),
            "WaitForFrame slot above range" => () => device.WaitForFrame(2),
            "WaitForFrame on an open slot" => Then(() => FakeBackendFixture.OpenFrame(device, 0), () => device.WaitForFrame(0)),
            "IsFrameComplete slot above range" => () => device.IsFrameComplete(2),
            "BeginFrame before WaitForFrame" => () => device.BeginFrame(0),
            "BeginFrame twice" => Then(() => FakeBackendFixture.OpenFrame(device, 0), () => device.BeginFrame(0)),
            "BeginFrame after a failed WaitForFrame" => Then(
                () =>
                {
                    _fixture.Script.FailNext(FakeBackendOperation.WaitForFrame, new EngineError(ErrorKind.Timeout, "hung"));
                    _ = device.WaitForFrame(0);
                },
                () => device.BeginFrame(0)),
            "AcquireImage outside an open frame" => () => device.AcquireImage(swapchain, 0),
            "Execute outside an open frame" => () => device.Execute(Plan(0, 1)),
            "Execute a slot twice" => Then(
                () =>
                {
                    FakeBackendFixture.OpenFrame(device, 0);
                    _ = device.Execute(Plan(0, 1));
                },
                () => device.Execute(Plan(0, 1))),
            "Execute waiting for an image not acquired" => Then(
                () => FakeBackendFixture.OpenFrame(device, 0),
                () => device.Execute(GraphicsInternals.NewFramePlan(0, 1, [], [new(swapchain, 0)]))),
            "Execute without an image it acquired" => Then(
                () =>
                {
                    FakeBackendFixture.OpenFrame(device, 0);
                    _ = device.AcquireImage(swapchain, 0);
                },
                () => device.Execute(Plan(0, 1))),
            "Execute waiting for another image of the swapchain" => Then(
                () => image = OpenAndAcquire(device, swapchain),
                () => device.Execute(GraphicsInternals.NewFramePlan(0, 1, [], [new(swapchain, (image + 1) % 3)]))),
            "Execute waiting for another swapchain's image" => Then(
                () =>
                {
                    (_, _, second) = Presentable(device);
                    image = OpenAndAcquire(device, swapchain);
                },
                () => device.Execute(GraphicsInternals.NewFramePlan(0, 1, [], [new(second, image)]))),
            "Execute listing an acquired image twice" => Then(
                () =>
                {
                    image = OpenAndAcquire(device, swapchain);
                    _ = device.AcquireImage(swapchain, 0);
                },
                () => device.Execute(GraphicsInternals.NewFramePlan(0, 1, [], [new(swapchain, image), new(swapchain, image)]))),
            "Present an image not executed" => Then(
                () =>
                {
                    FakeBackendFixture.OpenFrame(device, 0);
                    _ = device.Execute(Plan(0, 1));
                },
                () => device.Present(swapchain, 0)),
            "Present an image twice" => Then(
                () =>
                {
                    FakeBackendFixture.OpenFrame(device, 0);
                    _ = device.Present(swapchain, FakeBackendFixture.ExecuteWithImage(device, 0, swapchain));
                },
                () => device.Present(swapchain, 0)),
            "Present an image after its Execute failed" => Then(
                () =>
                {
                    image = OpenAndAcquire(device, swapchain);
                    FailedExecute(0, image);
                },
                () => device.Present(swapchain, image)),
            "Present an earlier frame's image after a failed Execute" => Then(
                () =>
                {
                    FakeBackendFixture.OpenFrame(device, 0);
                    image = FakeBackendFixture.ExecuteWithImage(device, 0, swapchain);
                    FakeBackendFixture.OpenFrame(device, 1);
                    FailedExecute(1, device.AcquireImage(swapchain, 1).ImageIndex);
                },
                () => device.Present(swapchain, image)),
            "TryReadTimestamps slot above range" => () => device.TryReadTimestamps(2, []),
            "WaitForFrame after Dispose" => Then(device.Dispose, () => device.WaitForFrame(0)),
            _ => throw new ArgumentOutOfRangeException(nameof(violation), violation, "Unknown row."),
        };

        static Action Then(Action setup, Action call)
        {
            setup();
            return call;
        }

        static int OpenAndAcquire(FakeBackendDevice device, ulong swapchain)
        {
            FakeBackendFixture.OpenFrame(device, 0);
            return device.AcquireImage(swapchain, 0).ImageIndex;
        }

        // A scripted submit failure for the open slot's plan, which waits for one acquired image.
        void FailedExecute(int slot, int acquiredImage)
        {
            _fixture.Script.FailNext(FakeBackendOperation.Execute, new EngineError(ErrorKind.Unavailable, "lost"));
            Assert.True(device.Execute(GraphicsInternals.NewFramePlan(slot, 2, [], [new(swapchain, acquiredImage)])).IsFailure);
        }
    }
}
