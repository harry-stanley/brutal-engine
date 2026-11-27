using System;
using BrutalEngine.Graphics.Tests.Backend;
using BrutalEngine.Graphics.Tests.Device;
using Xunit;
using static BrutalEngine.Graphics.Tests.Swapchains.SwapchainTestKit;

namespace BrutalEngine.Graphics.Tests.Swapchains;

/// <summary>Steady acquire, submit and present frames allocate no managed memory (docs/design/graphics.md section 14).</summary>
public sealed class SwapchainAllocationTests : IDisposable
{
    private const int Frames = 1000;
    private const int WarmUpFrames = 8;

    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void SteadyFrames_DoNotAllocate()
    {
        _fixture.Script.RecordCalls = false;
        FakeWindow window = Window();
        GraphicsDevice device = NewDevice(_fixture, window);
        SwapchainHandle swapchain = device.CreateSwapchain(window, new SwapchainOptions()).Value;
        BufferHandle rows = device.CreateBuffer(new BufferDescription { Size = 1024, Usage = BufferUsage.CopySource }).Value;

        // Even frames copy into the image (the scan finds the write), odd frames don't (the scan appends a clear); every
        // frame presents. The warm-up frames grow both slots' streams and the acquired lists.
        long checksum = 0;
        long before = 0;
        for (int i = 0; i < Frames + WarmUpFrames; i++)
        {
            if (i == WarmUpFrames)
            {
                before = GC.GetAllocatedBytesForCurrentThread();
            }

            _ = device.BeginFrame(out CommandList commands);
            AcquireStatus status = commands.AcquireSwapchainImage(swapchain, out SwapchainImage image);
            if (i % 2 == 0)
            {
                commands.CopyBufferToTexture(new TextureBufferCopy
                {
                    Texture = new TextureLocation(image.Texture),
                    Region = new TextureRegion(0, 0, 4, 4),
                    Buffer = rows,
                    BytesPerRow = 256,
                });
            }

            FrameStatus submitted = device.SubmitFrame(commands);
            checksum += image.Width + (status == AcquireStatus.Acquired && submitted == FrameStatus.Ready ? 0 : -100_000);
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocatedBytes);
        Assert.Equal((Frames + WarmUpFrames) * 640L, checksum);
        Assert.True(device.WaitIdle().IsSuccess);
        device.Destroy(rows);
        device.Destroy(swapchain);
        device.ThrowIfLeaks();
    }
}
