using System;
using System.Linq;
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
        // Objects made at load time, destroyed one per frame: inside the open frame on even frames, between frames
        // (onto the last submitted frame's queue) on odd ones, so both queue paths and their retirement run.
        SamplerHandle[] samplers = [.. Enumerable.Range(0, Frames + 8).Select(_ => device.CreateSampler(new SamplerDescription()).Value)];
        for (int i = 0; i < 8; i++)
        {
            RunFrame(device, samplers[i], insideFrame: i % 2 == 0);
        }

        long checksum = 0;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 8; i < samplers.Length; i++)
        {
            checksum += RunFrame(device, samplers[i], insideFrame: i % 2 == 0);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(samplers.Length, device.FrameNumber);
        Assert.NotEqual(0, checksum);
        Assert.True(device.WaitIdle().IsSuccess);
        device.ThrowIfLeaks();
    }

    private static long RunFrame(GraphicsDevice device, SamplerHandle sampler, bool insideFrame)
    {
        FrameStatus begun = device.BeginFrame(out CommandList commands);
        if (insideFrame)
        {
            device.Destroy(sampler);
        }

        GraphicsStatistics statistics = device.Statistics;
        FrameStatus submitted = device.SubmitFrame(commands);
        if (!insideFrame)
        {
            device.Destroy(sampler);
        }

        return (long)begun + (long)submitted + statistics.FramesSubmitted + device.FrameNumber + device.CompletedFrameNumber
            + (device.IsLost ? 1 : 0);
    }
}
