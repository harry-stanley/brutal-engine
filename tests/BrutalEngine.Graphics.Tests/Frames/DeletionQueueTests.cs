using System;
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

    [Fact]
    public void Destroy_ReachesBackendAfterLastFrame()
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex }).Value;
        TextureHandle texture = device.CreateTexture(_texture).Value;
        TextureViewHandle view = device.CreateTextureView(new TextureViewDescription { Texture = texture }).Value;
        ulong bufferId = backend.Objects.GetId(buffer);
        ulong textureId = backend.Objects.GetId(texture);
        ulong[] viewIds = [backend.Objects.GetId(device.GetDefaultView(texture)), backend.Objects.GetId(view)];
        int live = device.Statistics.LiveObjects;

        // Frame 0 (slot 0) is submitted; the buffer is destroyed between frames, so it waits for frame 0.
        RunFrame(device);
        device.Destroy(buffer);
        Assert.Equal(live - 1, device.Statistics.LiveObjects);
        Assert.Throws<StaleHandleException>(() => device.Destroy(buffer));

        // Frame 1 (slot 1) doesn't wait for frame 0, so the buffer stays; the texture is destroyed inside frame 1.
        int start = backend.Calls.Count;
        _ = device.BeginFrame(out CommandList commands);
        device.Destroy(texture);
        _ = device.SubmitFrame(commands);
        Assert.DoesNotContain(CallsSince(start), call => call.Operation == Op.Destroy);
        Assert.Equal(live - 4, device.Statistics.LiveObjects);

        // Frame 2 (slot 0) waits for frame 0, then the buffer goes, before the slot opens.
        start = backend.Calls.Count;
        _ = device.BeginFrame(out commands);
        Assert.Equal(
            [Slot(Op.WaitForFrame, 0), Destroyed(BackendObjectKind.Buffer, bufferId), Slot(Op.BeginFrame, 0)],
            CallsSince(start));
        _ = device.SubmitFrame(commands);

        // Frame 3 (slot 1) waits for frame 1: the texture's views, then the texture.
        start = backend.Calls.Count;
        _ = device.BeginFrame(out commands);
        FakeBackendCall[] calls = CallsSince(start);
        Assert.Equal(5, calls.Length);
        Assert.Equal(Slot(Op.WaitForFrame, 1), calls[0]);
        Assert.Equal(viewIds.Order(), calls[1..3].Select(call => call.Id).Order());
        Assert.All(calls[1..3], call => Assert.Equal((Op.Destroy, BackendObjectKind.TextureView), (call.Operation, call.Kind)));
        Assert.Equal(Destroyed(BackendObjectKind.Texture, textureId), calls[3]);
        Assert.Equal(Slot(Op.BeginFrame, 1), calls[4]);
        _ = device.SubmitFrame(commands);
    }

    [Fact]
    public void CreateAndDestroyInFrame_DestroyedAfterFrame()
    {
        GraphicsDevice device = _fixture.Create();
        FakeBackendDevice backend = _fixture.BackendDevice;
        int start = backend.Calls.Count;

        // Frame 0 is open, so nothing has been submitted yet, but the frame may use the sampler.
        _ = device.BeginFrame(out CommandList commands);
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        ulong id = backend.Objects.GetId(sampler);
        device.Destroy(sampler);
        _ = device.SubmitFrame(commands);
        RunFrame(device);
        Assert.DoesNotContain(CallsSince(start), call => call.Operation == Op.Destroy);

        start = backend.Calls.Count;
        _ = device.BeginFrame(out commands);

        Assert.Equal([Slot(Op.WaitForFrame, 0), Destroyed(BackendObjectKind.Sampler, id), Slot(Op.BeginFrame, 0)], CallsSince(start));
        _ = device.SubmitFrame(commands);
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

    private FakeBackendCall[] CallsSince(int start) => [.. _fixture.BackendDevice.Calls.Skip(start)];
}
