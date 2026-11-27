using System;
using BrutalEngine.Graphics.Tests.Device;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Frames;

/// <summary>Steady-state frames allocate no managed memory (docs/design/graphics.md section 14).</summary>
public sealed class FrameAllocationTests : IDisposable
{
    private const int Frames = 1000;

    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void SteadyFrames_DoNotAllocate()
    {
        _fixture.Script.RecordCalls = false;
        GraphicsDevice device = _fixture.Create();
        for (int i = 0; i < 8; i++)
        {
            _ = RunFrame(device);
        }

        long checksum = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Frames; i++)
        {
            checksum += RunFrame(device);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(8 + Frames, device.FrameNumber);
        Assert.NotEqual(0, checksum);
        Assert.True(device.WaitIdle().IsSuccess);
    }

    private static long RunFrame(GraphicsDevice device)
    {
        FrameStatus begun = device.BeginFrame(out CommandList commands);
        GraphicsStatistics statistics = device.Statistics;
        FrameStatus submitted = device.SubmitFrame(commands);
        return (long)begun + (long)submitted + statistics.FramesSubmitted + device.FrameNumber + device.CompletedFrameNumber
            + (device.IsLost ? 1 : 0);
    }
}
