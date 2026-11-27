using System;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Graphics.Tests.Device;
using Xunit;

namespace BrutalEngine.Graphics.Tests.DeviceLoss;

/// <summary>
/// The fault seam costs steady frames nothing, and neither do a lost device's frames (docs/design/graphics.md sections
/// 12 and 14).
/// </summary>
public sealed class DeviceLossAllocationTests : IDisposable
{
    private const int Frames = 1000;
    private const int WarmUpFrames = 8;

    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void ArmedSeamAndLostFrames_DoNotAllocate()
    {
        _fixture.Script.RecordCalls = false;
        var rig = new LossRig(_fixture);
        GraphicsDevice device = rig.Device;
        // Armed for a call the frames never make, so every gate in the loop asks the seam and is told no.
        device.Faults.InjectLoss(FaultSeam.Operation.CreateSampler, 1, new EngineError(ErrorKind.Unavailable, "unused", -4));
        long checksum = 0;
        long before = 0;
        for (int i = 0; i < Frames + WarmUpFrames; i++)
        {
            before = i == WarmUpFrames ? GC.GetAllocatedBytesForCurrentThread() : before;
            (FrameStatus begun, AcquireStatus acquired, FrameStatus submitted) = rig.Frame();
            checksum += begun == FrameStatus.Ready && acquired == AcquireStatus.Acquired && submitted == FrameStatus.Ready ? 1 : -100_000;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(Frames + WarmUpFrames, checksum);

        // Then the device is lost (outside the window: the detection logs and raises the event). Its frames, with
        // acquires, write refusals of the seam's gates and the lost checks, allocate nothing either.
        device.Faults.InjectLoss(FaultSeam.Operation.Execute, 1, new EngineError(ErrorKind.Unavailable, "lost", -4));
        Assert.Equal(FrameStatus.DeviceLost, rig.Frame().Submitted);
        checksum = 0;
        before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Frames; i++)
        {
            (FrameStatus begun, AcquireStatus acquired, FrameStatus submitted) = rig.Frame();
            checksum += begun == FrameStatus.DeviceLost && acquired == AcquireStatus.DeviceLost && submitted == FrameStatus.DeviceLost ? 1 : -100_000;
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(Frames, checksum);
        Assert.Single(rig.Events);
    }
}
