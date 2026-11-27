using System;
using System.Collections.Generic;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Tests.Device;
using BrutalEngine.TestUtilities;
using Xunit;
using Op = BrutalEngine.TestUtilities.FakeBackendOperation;

namespace BrutalEngine.Graphics.Tests.Frames;

/// <summary>Deferred destruction through the frame slots' deletion queues (docs/design/graphics.md sections 1 and 4).</summary>
public sealed class DeletionQueueTests : IDisposable
{
    private static readonly TextureDescription _texture = new()
    {
        Format = TextureFormat.Rgba8Unorm,
        Width = 4,
        Height = 4,
        Usage = TextureUsage.Sampled,
        DebugName = "Queued",
    };

    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Destroy_ReachesBackendAfterLastFrame(int framesInFlight)
    {
        GraphicsDevice device = _fixture.Create(_fixture.Options() with { FramesInFlight = framesInFlight });
        FakeBackendDevice backend = _fixture.BackendDevice;
        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex }).Value;
        TextureHandle texture = device.CreateTexture(_texture).Value;
        TextureViewHandle view = device.CreateTextureView(new TextureViewDescription { Texture = texture }).Value;
        ulong bufferId = backend.Objects.GetId(buffer);
        ulong textureId = backend.Objects.GetId(texture);
        ulong[] viewIds = [backend.Objects.GetId(device.GetDefaultView(texture)), backend.Objects.GetId(view)];
        int live = device.Statistics.LiveObjects;
        int start = backend.Calls.Count;
        var begins = new List<FrameBegin>();

        // Frame 0 is submitted; the buffer is destroyed between frames, so its last frame is 0.
        RunFrame(device, begins);
        device.Destroy(buffer);
        Assert.Equal(live - 1, device.Statistics.LiveObjects);
        Assert.Throws<StaleHandleException>(() => device.Destroy(buffer));

        // The texture is destroyed inside frame 1, so its last frame is 1.
        CommandList commands = Begin(device, begins);
        device.Destroy(texture);
        Assert.Equal(live - 4, device.Statistics.LiveObjects);
        _ = device.SubmitFrame(commands);
        while (device.FrameNumber <= 1 + framesInFlight)
        {
            RunFrame(device, begins);
        }

        // Each object reaches the backend only in the BeginFrame of its last frame plus the frames in flight, between
        // that slot's wait (which completed the last frame) and its open.
        foreach (FrameBegin begin in begins)
        {
            int slot = (int)(begin.Frame % framesInFlight);
            Assert.Equal(Slot(Op.WaitForFrame, slot), begin.Calls[0]);
            Assert.Equal(Slot(Op.BeginFrame, slot), begin.Calls[^1]);
            FakeBackendCall[] destroyed = begin.Calls[1..^1];
            if (begin.Frame == framesInFlight)
            {
                AssertCompletes(begin, lastFrame: 0);
                Assert.Equal([Destroyed(BackendObjectKind.Buffer, bufferId)], destroyed);
            }
            else if (begin.Frame == 1 + framesInFlight)
            {
                // The texture's views, then the texture.
                AssertCompletes(begin, lastFrame: 1);
                Assert.Equal(3, destroyed.Length);
                Assert.Equal(viewIds.Order(), destroyed[..2].Select(call => call.Id).Order());
                Assert.All(destroyed[..2], call => Assert.Equal((Op.Destroy, BackendObjectKind.TextureView), (call.Operation, call.Kind)));
                Assert.Equal(Destroyed(BackendObjectKind.Texture, textureId), destroyed[2]);
            }
            else
            {
                Assert.Empty(destroyed);
            }
        }

        Assert.Equal(4, CallsSince(start).Count(call => call.Operation == Op.Destroy));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Destroy_BetweenAndInsideEachSlotsFrames_ReachesBackendOnlyWhenLastFrameCompletes(int framesInFlight)
    {
        GraphicsDevice device = _fixture.Create(_fixture.Options() with { FramesInFlight = framesInFlight });
        FakeBackendDevice backend = _fixture.BackendDevice;
        int start = backend.Calls.Count;
        var lastFrames = new Dictionary<ulong, long>();
        var begins = new List<FrameBegin>();

        // Every slot, twice: one sampler destroyed between frames (its last frame is the one just submitted) and one
        // inside each frame (its last frame is the open one).
        long frames = 2 * framesInFlight;
        for (long frame = 0; frame < frames; frame++)
        {
            if (frame > 0)
            {
                lastFrames.Add(DestroyNewSampler(device), frame - 1);
            }

            CommandList commands = Begin(device, begins);
            lastFrames.Add(DestroyNewSampler(device), frame);
            _ = device.SubmitFrame(commands);
        }

        while (device.FrameNumber <= frames - 1 + framesInFlight)
        {
            RunFrame(device, begins);
        }

        var destroyed = new List<ulong>();
        foreach (FrameBegin begin in begins)
        {
            foreach (FakeBackendCall call in begin.Calls.Where(call => call.Operation == Op.Destroy))
            {
                long lastFrame = lastFrames[call.Id];
                Assert.Equal(lastFrame + framesInFlight, begin.Frame);
                AssertCompletes(begin, lastFrame);
                destroyed.Add(call.Id);
            }
        }

        // Each sampler reached the backend exactly once, and never outside a BeginFrame.
        Assert.Equal(lastFrames.Keys.Order(), destroyed.Order());
        Assert.Equal(lastFrames.Count, CallsSince(start).Count(call => call.Operation == Op.Destroy));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CreateAndDestroyInFrame_DestroyedAfterFrame(int framesInFlight)
    {
        GraphicsDevice device = _fixture.Create(_fixture.Options() with { FramesInFlight = framesInFlight });
        FakeBackendDevice backend = _fixture.BackendDevice;
        int start = backend.Calls.Count;
        var begins = new List<FrameBegin>();

        // Frame 0 is open, so nothing has been submitted yet, but the frame may use the sampler.
        CommandList commands = Begin(device, begins);
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        ulong id = backend.Objects.GetId(sampler);
        device.Destroy(sampler);
        _ = device.SubmitFrame(commands);
        while (device.FrameNumber <= framesInFlight)
        {
            RunFrame(device, begins);
        }

        // Only the BeginFrame that waited for frame 0 (frame 0 plus the frames in flight, slot 0) destroys it.
        foreach (FrameBegin begin in begins[..^1])
        {
            Assert.DoesNotContain(begin.Calls, call => call.Operation == Op.Destroy);
        }

        FrameBegin last = begins[^1];
        Assert.Equal(framesInFlight, last.Frame);
        AssertCompletes(last, lastFrame: 0);
        Assert.Equal([Slot(Op.WaitForFrame, 0), Destroyed(BackendObjectKind.Sampler, id), Slot(Op.BeginFrame, 0)], last.Calls);
        Assert.Single(CallsSince(start), call => call.Operation == Op.Destroy);
    }

    [Fact]
    public void Destroy_BeforeFirstFrame_IsImmediate()
    {
        GraphicsDevice device = _fixture.Create();
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        ulong id = _fixture.BackendDevice.Objects.GetId(sampler);

        device.Destroy(sampler);

        Assert.Equal(Destroyed(BackendObjectKind.Sampler, id), _fixture.BackendDevice.Calls[^1]);
    }

    [Fact]
    public void Destroy_ManyInOneFrame_QueueGrowsAndKeepsOrder()
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        SamplerHandle[] samplers = [.. Enumerable.Range(0, 150).Select(_ => device.CreateSampler(new SamplerDescription()).Value)];
        ulong[] ids = [.. samplers.Select(backend.Objects.GetId)];

        _ = device.BeginFrame(out CommandList commands);
        foreach (SamplerHandle sampler in samplers)
        {
            device.Destroy(sampler);
        }

        _ = device.SubmitFrame(commands);
        RunFrame(device);
        int start = backend.Calls.Count;
        RunFrame(device);

        Assert.Equal(ids, CallsSince(start).Where(call => call.Operation == Op.Destroy).Select(call => call.Id));
    }

    [Fact]
    public void Destroy_QueueCannotGrow_ThrowsAndObjectStaysLive()
    {
        var failing = new FailingAllocator(_fixture.Allocator);
        GraphicsDevice device = _fixture.Create(_fixture.Options() with { Allocator = failing });
        SamplerHandle[] samplers = [.. Enumerable.Range(0, 65).Select(_ => device.CreateSampler(new SamplerDescription()).Value)];
        _ = device.BeginFrame(out CommandList commands);
        // A queue starts with room for 64 objects.
        foreach (SamplerHandle sampler in samplers[..64])
        {
            device.Destroy(sampler);
        }

        int live = device.Statistics.LiveObjects;
        failing.FailNext = true;

        Assert.ThrowsAny<OutOfMemoryException>(() => device.Destroy(samplers[64]));

        Assert.Equal(live, device.Statistics.LiveObjects);
        device.Destroy(samplers[64]);
        Assert.Equal(live - 1, device.Statistics.LiveObjects);
        _ = device.SubmitFrame(commands);
    }

    [Fact]
    public void DestroyTexture_QueueCannotGrowForSecondView_FirstViewQueuedRestLive()
    {
        var failing = new FailingAllocator(_fixture.Allocator);
        GraphicsDevice device = _fixture.Create(_fixture.Options() with { Allocator = failing });
        FakeBackendDevice backend = _fixture.BackendDevice;
        SamplerHandle[] samplers = [.. Enumerable.Range(0, 63).Select(_ => device.CreateSampler(new SamplerDescription()).Value)];
        TextureHandle texture = device.CreateTexture(_texture).Value;
        TextureViewHandle view = device.CreateTextureView(new TextureViewDescription { Texture = texture }).Value;
        TextureViewHandle defaultView = device.GetDefaultView(texture);
        _ = device.BeginFrame(out CommandList commands);
        foreach (SamplerHandle sampler in samplers)
        {
            device.Destroy(sampler);
        }

        // The 64th entry fits; making room for the 65th fails.
        failing.FailNext = true;
        Assert.ThrowsAny<OutOfMemoryException>(() => device.Destroy(texture));

        // The default view (made first) is gone and queued; the texture and its other view are still live.
        Assert.Throws<StaleHandleException>(() => backend.Objects.GetId(defaultView));
        Assert.NotEqual(0UL, backend.Objects.GetId(view));
        Assert.NotEqual(0UL, backend.Objects.GetId(texture));
        Assert.Equal(2, device.Statistics.LiveObjects);

        // Destroying the texture again finishes the job.
        device.Destroy(texture);
        Assert.Equal(0, device.Statistics.LiveObjects);
        _ = device.SubmitFrame(commands);
        RunFrame(device);
        int start = backend.Calls.Count;
        RunFrame(device);

        // Everything still reached the backend: 63 samplers, both views, then the texture last.
        FakeBackendCall[] destroyed = [.. CallsSince(start).Where(call => call.Operation == Op.Destroy)];
        Assert.Equal(63 + 2 + 1, destroyed.Length);
        Assert.Equal(BackendObjectKind.Texture, destroyed[^1].Kind);
    }

    private static FakeBackendCall Slot(Op operation, int frameSlot) => new(operation, null, 0, frameSlot);

    private static FakeBackendCall Destroyed(BackendObjectKind kind, ulong id) => new(Op.Destroy, kind, id, -1);

    private static void RunFrame(GraphicsDevice device)
    {
        _ = device.BeginFrame(out CommandList commands);
        _ = device.SubmitFrame(commands);
    }

    private void RunFrame(GraphicsDevice device, List<FrameBegin> begins) => _ = device.SubmitFrame(Begin(device, begins));

    // Opens the next frame and records the backend calls its BeginFrame made, with CompletedFrameNumber around it.
    private CommandList Begin(GraphicsDevice device, List<FrameBegin> begins)
    {
        long frame = device.FrameNumber;
        long completedBefore = device.CompletedFrameNumber;
        int start = _fixture.BackendDevice.Calls.Count;
        Assert.Equal(FrameStatus.Ready, device.BeginFrame(out CommandList commands));
        begins.Add(new FrameBegin(frame, completedBefore, device.CompletedFrameNumber, CallsSince(start)));
        return commands;
    }

    // The wait in this BeginFrame is what completed the object's last frame: not done before it, done after it.
    private static void AssertCompletes(FrameBegin begin, long lastFrame)
    {
        Assert.True(begin.CompletedBefore < lastFrame, $"frame {lastFrame} was already complete before frame {begin.Frame} began");
        Assert.True(begin.CompletedAfter >= lastFrame, $"frame {lastFrame} wasn't complete after frame {begin.Frame} began");
    }

    private ulong DestroyNewSampler(GraphicsDevice device)
    {
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        ulong id = _fixture.BackendDevice.Objects.GetId(sampler);
        device.Destroy(sampler);
        return id;
    }

    private FakeBackendCall[] CallsSince(int start) => [.. _fixture.BackendDevice.Calls.Skip(start)];

    /// <summary>One BeginFrame: the frame it opened, CompletedFrameNumber before and after it, and its backend calls.</summary>
    private sealed record FrameBegin(long Frame, long CompletedBefore, long CompletedAfter, FakeBackendCall[] Calls);
}
