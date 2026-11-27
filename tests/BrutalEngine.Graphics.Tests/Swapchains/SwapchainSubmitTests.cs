using System;
using System.Collections.Generic;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Tests.Backend;
using BrutalEngine.Graphics.Tests.Device;
using BrutalEngine.TestUtilities;
using Xunit;
using static BrutalEngine.Graphics.Tests.Swapchains.SwapchainTestKit;

namespace BrutalEngine.Graphics.Tests.Swapchains;

/// <summary>
/// Acquired images at <c>SubmitFrame</c>: the plan's acquired list, the clear of images no copy wrote, and the presents
/// after <c>Execute</c> with their status mapping (docs/design/graphics.md section 3 and "The backend interface").
/// </summary>
public sealed class SwapchainSubmitTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void UnwrittenImage_IsCleared()
    {
        FakeWindow first = Window();
        GraphicsDevice device = NewDevice(_fixture, first);
        FakeBackendDevice backend = _fixture.BackendDevice;
        _fixture.Script.RecordPlans = true;
        SwapchainHandle a = device.CreateSwapchain(first, new SwapchainOptions()).Value;
        ulong aId = LastId(backend, FakeBackendOperation.CreateSwapchain);
        SwapchainHandle b = device.CreateSwapchain(Window(), new SwapchainOptions()).Value;
        ulong bId = LastId(backend, FakeBackendOperation.CreateSwapchain);
        BufferHandle rows = device.CreateBuffer(new BufferDescription { Size = 1024, Usage = BufferUsage.CopySource }).Value;
        TextureHandle source = device.CreateTexture(new TextureDescription
        {
            Format = TextureFormat.Bgra8UnormSrgb,
            Width = 8,
            Height = 8,
            Usage = TextureUsage.CopySource,
        }).Value;

        // Frame 0 writes b's image from a buffer, frame 1 writes a's from a texture: the other image is cleared each time.
        for (int frame = 0; frame < 2; frame++)
        {
            _ = device.BeginFrame(out CommandList commands);
            Assert.Equal(AcquireStatus.Acquired, commands.AcquireSwapchainImage(a, out SwapchainImage imageA));
            Assert.Equal(AcquireStatus.Acquired, commands.AcquireSwapchainImage(b, out SwapchainImage imageB));
            if (frame == 0)
            {
                commands.CopyBufferToTexture(new TextureBufferCopy
                {
                    Texture = new TextureLocation(imageB.Texture),
                    Region = new TextureRegion(0, 0, 4, 4),
                    Buffer = rows,
                    BytesPerRow = 256,
                });
            }
            else
            {
                commands.CopyTextureToTexture(new TextureCopy
                {
                    Source = new TextureLocation(source),
                    SourceRegion = new TextureRegion(0, 0, 8, 8),
                    Destination = new TextureLocation(imageA.Texture),
                });
            }

            int calls = backend.Calls.Count;
            Assert.Equal(FrameStatus.Ready, device.SubmitFrame(commands));

            RecordedPlan plan = backend.Plans[^1];
            Assert.Equal([new SwapchainImageUse(aId, frame), new SwapchainImageUse(bId, frame)], plan.Acquired.ToArray());
            // One clear, last, for the image no copy wrote; then Execute, then a present per image in acquire order.
            Assert.Equal([frame == 0 ? 0 : 1], Clears(plan));
            Assert.Equal(CommandKind.ClearSwapchainImage, Kinds(plan)[^1]);
            Assert.Equal(
                [(FakeBackendOperation.Execute, 0UL), (FakeBackendOperation.Present, aId), (FakeBackendOperation.Present, bId)],
                backend.Calls.Skip(calls).Select(call => (call.Operation, call.Operation == FakeBackendOperation.Present ? call.Id : 0UL)));
        }

        device.Destroy(source);
        device.Destroy(rows);
    }

    [Fact]
    public void WrittenByWriteTexture_NotCleared()
    {
        FakeWindow first = Window();
        GraphicsDevice device = NewDevice(_fixture, first);
        FakeBackendDevice backend = _fixture.BackendDevice;
        _fixture.Script.RecordPlans = true;
        SwapchainHandle a = device.CreateSwapchain(first, new SwapchainOptions()).Value;
        SwapchainHandle b = device.CreateSwapchain(Window(), new SwapchainOptions()).Value;
        byte[] texels = new byte[4 * 4 * 4];

        // A WriteTexture lands through the frame's upload stream, not its commands: frame 0 writes a's image, frame 1 b's,
        // and only the other image is cleared, after the write.
        for (int frame = 0; frame < 2; frame++)
        {
            _ = device.BeginFrame(out CommandList commands);
            Assert.Equal(AcquireStatus.Acquired, commands.AcquireSwapchainImage(a, out SwapchainImage imageA));
            Assert.Equal(AcquireStatus.Acquired, commands.AcquireSwapchainImage(b, out SwapchainImage imageB));
            TextureHandle written = frame == 0 ? imageA.Texture : imageB.Texture;
            Assert.True(device.WriteTexture(new TextureWrite { Target = new(written), Region = new(0, 0, 4, 4) }, texels).IsSuccess);

            Assert.Equal(FrameStatus.Ready, device.SubmitFrame(commands));

            RecordedPlan plan = backend.Plans[^1];
            Assert.Equal([CommandKind.CopyBufferToTexture, CommandKind.ClearSwapchainImage], Kinds(plan));
            Assert.Equal([frame == 0 ? 1 : 0], Clears(plan));
        }
    }

    [Theory]
    [InlineData(SwapchainStatus.Success, FrameStatus.Ready, AcquireStatus.Acquired)]
    [InlineData(SwapchainStatus.Suboptimal, FrameStatus.Ready, AcquireStatus.Recreated)]
    [InlineData(SwapchainStatus.OutOfDate, FrameStatus.Ready, AcquireStatus.Recreated)]
    [InlineData(SwapchainStatus.SurfaceLost, FrameStatus.Ready, AcquireStatus.SurfaceLost)]
    [InlineData(SwapchainStatus.DeviceLost, FrameStatus.DeviceLost, AcquireStatus.DeviceLost)]
    public void PresentStatuses_Map(SwapchainStatus present, FrameStatus expected, AcquireStatus nextAcquire)
    {
        FakeWindow window = Window();
        GraphicsDevice device = NewDevice(_fixture, window);
        SwapchainHandle swapchain = device.CreateSwapchain(window, new SwapchainOptions()).Value;
        _ = device.BeginFrame(out CommandList commands);
        Assert.Equal(AcquireStatus.Acquired, commands.AcquireSwapchainImage(swapchain, out _));
        _fixture.Script.SwapchainStatusNext(FakeBackendOperation.Present, present, -5);

        Assert.Equal(expected, device.SubmitFrame(commands));

        Assert.Equal(present == SwapchainStatus.DeviceLost, device.IsLost);
        if (device.IsLost)
        {
            Assert.Equal(("SubmitFrame", -5), (device.LostOperation, device.LostNativeCode));
        }

        // The status shows at the next acquire: recreated after a suboptimal or out-of-date present, or still lost.
        _ = device.BeginFrame(out commands);
        Assert.Equal(nextAcquire, commands.AcquireSwapchainImage(swapchain, out _));
        _ = device.SubmitFrame(commands);
        Assert.Equal(nextAcquire == AcquireStatus.Recreated ? 2 : 1, _fixture.BackendDevice.Calls.Count(call => call.Operation == FakeBackendOperation.CreateSwapchain));
    }

    [Fact]
    public void ExecuteFails_NothingPresented()
    {
        FakeWindow window = Window();
        GraphicsDevice device = NewDevice(_fixture, window);
        SwapchainHandle swapchain = device.CreateSwapchain(window, new SwapchainOptions()).Value;
        _ = device.BeginFrame(out CommandList commands);
        Assert.Equal(AcquireStatus.Acquired, commands.AcquireSwapchainImage(swapchain, out _));
        _fixture.Script.FailNext(FakeBackendOperation.Execute, new EngineError(ErrorKind.Unavailable, "lost", -4));

        Assert.Equal(FrameStatus.DeviceLost, device.SubmitFrame(commands));

        Assert.DoesNotContain(_fixture.BackendDevice.Calls, call => call.Operation == FakeBackendOperation.Present);
    }

    private static List<CommandKind> Kinds(RecordedPlan plan)
    {
        var kinds = new List<CommandKind>();
        var reader = new CommandReader(plan.Commands);
        while (reader.MoveNext())
        {
            kinds.Add(reader.Kind);
        }

        return kinds;
    }

    private static List<int> Clears(RecordedPlan plan)
    {
        var clears = new List<int>();
        var reader = new CommandReader(plan.Commands);
        while (reader.MoveNext())
        {
            if (reader.Kind == CommandKind.ClearSwapchainImage)
            {
                clears.Add(reader.Payload<ClearSwapchainImageCommand>().AcquiredIndex);
            }
        }

        return clears;
    }
}
