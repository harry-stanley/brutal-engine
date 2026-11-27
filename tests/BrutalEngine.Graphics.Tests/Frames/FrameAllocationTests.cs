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

    [Fact]
    public void SteadyFramesWithWrites_DoNotAllocate()
    {
        _fixture.Script.RecordCalls = false;
        GraphicsDevice device = _fixture.Create();
        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 4096, Usage = BufferUsage.CopyDestination | BufferUsage.Uniform }).Value;
        TextureHandle texture = device.CreateTexture(new TextureDescription
        {
            Format = TextureFormat.Rgba8Unorm,
            Width = 64,
            Height = 64,
            Usage = TextureUsage.CopyDestination | TextureUsage.Sampled,
        }).Value;
        byte[] uniforms = new byte[256];
        byte[] texels = new byte[(16 * 4 * 15) + (10 * 4)];
        var write = new TextureWrite { Target = new(texture), Region = new(4, 4, 10, 16), BytesPerRow = 16 * 4 };

        // Every frame writes a buffer and a texture inside the ring, and the buffer again between frames, through a
        // pending block that the warm-up frames have already made and returned.
        long checksum = 0;
        long before = 0;
        for (int i = 0; i < Frames + 8; i++)
        {
            if (i == 8)
            {
                before = GC.GetAllocatedBytesForCurrentThread();
            }

            uniforms[0] = (byte)i;
            FrameStatus begun = device.BeginFrame(out CommandList commands);
            bool written = device.WriteBuffer(buffer, 0, uniforms).IsSuccess;
            written &= device.WriteTexture(write, texels).IsSuccess;
            FrameStatus submitted = device.SubmitFrame(commands);
            written &= device.WriteBuffer(buffer, 256, uniforms.AsSpan(0, 16)).IsSuccess;
            GraphicsStatistics statistics = device.Statistics;
            checksum += (long)begun + (long)submitted + statistics.UploadBytesLastFrame + statistics.UploadOverflowBytes + (written ? 1 : -100_000);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        // Each frame uploaded 256 + 10 * 16 * 4 bytes in its ring and the 16 pending bytes before it, with no overflow.
        Assert.Equal((Frames + 8) * (1 + 256 + 640 + 16L) - 16, checksum);
        Assert.True(device.WaitIdle().IsSuccess);
        device.Destroy(texture);
        device.Destroy(buffer);
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
