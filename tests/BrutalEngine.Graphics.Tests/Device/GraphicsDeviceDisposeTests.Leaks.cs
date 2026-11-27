using System;
using System.Collections.Generic;
using System.Linq;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.TestUtilities;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>The leak report and the full disposal (docs/design/graphics.md sections 1 and 13).</summary>
public sealed partial class GraphicsDeviceDisposeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Leaks_ListedAndDestroyedInReverse(bool fallingIds)
    {
        (GraphicsDevice device, BackendObjectTable objects) = CreateLeakDevice(fallingIds);
        FakeBackendDevice backend = _fixture.BackendDevice;
        // Out of kind order, with a reused slot: the newest buffer takes slot 0, so neither kind order nor slot order is
        // creation order. The ids rise in creation order on one row and fall on the other, so id order isn't either.
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        BufferHandle destroyed = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Index, DebugName = "Gone" }).Value;
        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex, DebugName = "Vertices" }).Value;
        TextureHandle texture = device.CreateTexture(Prepared.Texture with { DebugName = "Atlas" }).Value;
        device.Destroy(destroyed);
        BufferHandle indices = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Index, DebugName = "Indices" }).Value;
        Assert.Equal(0u, indices.Value.Index);
        ulong[] ids =
        [
            objects.GetId(sampler), objects.GetId(buffer), objects.GetId(texture), objects.GetId(device.GetDefaultView(texture)),
            objects.GetId(indices),
        ];
        Assert.Equal(fallingIds ? ids.OrderDescending() : ids.Order(), ids);
        int before = backend.Calls.Count;

        device.Dispose();

        // Logged oldest first, destroyed newest first.
        Assert.Equal(
            [
                (LogLevel.Error, "Graphics", "Leak: Sampler '' was still alive when the device was disposed."),
                (LogLevel.Error, "Graphics", "Leak: Buffer 'Vertices' was still alive when the device was disposed."),
                (LogLevel.Error, "Graphics", "Leak: Texture 'Atlas' was still alive when the device was disposed."),
                (LogLevel.Error, "Graphics", "Leak: TextureView 'Atlas' was still alive when the device was disposed."),
                (LogLevel.Error, "Graphics", "Leak: Buffer 'Indices' was still alive when the device was disposed."),
            ],
            _fixture.Entries());
        Assert.Equal(
            Enumerable.Reverse(ids).Select(id => (FakeBackendOperation.Destroy, fallingIds ? ReversedIdBackendDevice.Flip(id) : id))
                .Append((FakeBackendOperation.Dispose, 0UL)),
            backend.Calls.Skip(before).Select(call => (call.Operation, call.Id)));
        Assert.True(backend.IsDisposed);
    }

    [Fact]
    public void Dispose_NoLeaks_LogsNothing()
    {
        GraphicsDevice device = _fixture.Create();
        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex }).Value;
        device.Destroy(buffer);
        int before = _fixture.BackendDevice.Calls.Count;

        device.Dispose();

        Assert.Empty(_fixture.Entries());
        Assert.Equal([FakeBackendOperation.Dispose], _fixture.BackendDevice.Calls.Skip(before).Select(call => call.Operation));
    }

    [Fact]
    public void Dispose_Twice_LogsAndDestroysOnce()
    {
        GraphicsDevice device = _fixture.Create();
        _ = device.CreateSampler(new SamplerDescription { DebugName = "Once" }).Value;

        device.Dispose();
        device.Dispose();

        Assert.Single(_fixture.Entries());
        Assert.Single(_fixture.BackendDevice.Calls, call => call.Operation == FakeBackendOperation.Destroy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowIfLeaks_ListsEveryLiveObject(bool fallingIds)
    {
        (GraphicsDevice device, BackendObjectTable objects) = CreateLeakDevice(fallingIds);
        // Out of kind order, with a reused slot: the newest buffer takes slot 0, so neither kind order nor slot order is
        // creation order. The ids rise in creation order on one row and fall on the other, so id order isn't either.
        SamplerHandle sampler = device.CreateSampler(new SamplerDescription()).Value;
        BufferHandle destroyed = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Index, DebugName = "Gone" }).Value;
        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex, DebugName = "Vertices" }).Value;
        TextureHandle texture = device.CreateTexture(Prepared.Texture with { DebugName = "Atlas" }).Value;
        TextureViewHandle view = device.CreateTextureView(new TextureViewDescription { Texture = texture, DebugName = "Mip 0" }).Value;
        device.Destroy(destroyed);
        BufferHandle indices = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Index, DebugName = "Indices" }).Value;
        Assert.Equal(0u, indices.Value.Index);
        ulong[] ids =
        [
            objects.GetId(sampler), objects.GetId(buffer), objects.GetId(texture), objects.GetId(device.GetDefaultView(texture)),
            objects.GetId(view), objects.GetId(indices),
        ];
        Assert.Equal(fallingIds ? ids.OrderDescending() : ids.Order(), ids);
        int calls = _fixture.BackendDevice.Calls.Count;

        GpuResourceLeakException thrown = Assert.Throws<GpuResourceLeakException>(device.ThrowIfLeaks);

        // Oldest first.
        string[] expected = ["Sampler ''", "Buffer 'Vertices'", "Texture 'Atlas'", "TextureView 'Atlas'", "TextureView 'Mip 0'", "Buffer 'Indices'"];
        Assert.Equal(expected, thrown.LeakedObjects);
        Assert.Equal($"6 GPU object(s) still alive: {string.Join(", ", expected)}.", thrown.Message);
        // Nothing was destroyed: the objects are still live and usable.
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
        Assert.Equal(6, device.Statistics.LiveObjects);
        device.Destroy(view);
        device.Destroy(texture);
        device.Destroy(buffer);
        device.Destroy(indices);
        device.Destroy(sampler);
        device.ThrowIfLeaks();
    }

    [Fact]
    public void ThrowIfLeaks_NoLeaks_DoesNotAllocate()
    {
        GraphicsDevice device = _fixture.Create();
        device.ThrowIfLeaks();

        long before = GC.GetAllocatedBytesForCurrentThread();
        device.ThrowIfLeaks();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void GpuResourceLeakException_Constructors()
    {
        Assert.Equal("leakedObjects", Assert.Throws<ArgumentNullException>(() => new GpuResourceLeakException((IReadOnlyList<string>)null!)).ParamName);
        Assert.Empty(new GpuResourceLeakException().LeakedObjects);
        Assert.Equal("GPU objects were leaked.", new GpuResourceLeakException().Message);
        var inner = new InvalidOperationException("inner");
        var custom = new GpuResourceLeakException("custom", inner);
        Assert.Equal(("custom", inner), (custom.Message, custom.InnerException));
        Assert.Empty(custom.LeakedObjects);
        Assert.Equal("only", new GpuResourceLeakException("only").Message);
        Assert.Equal("0 GPU object(s) still alive: .", new GpuResourceLeakException(Array.Empty<string>()).Message);
    }

    [Fact]
    public void GpuResourceLeakException_CallerChangesList_ExceptionUnchanged()
    {
        string[] lines = ["Buffer 'Vertices'", "Sampler ''"];
        var list = new List<string>(lines);

        var thrown = new GpuResourceLeakException(list);
        list[0] = "Texture 'Changed'";
        list.Add("Buffer 'Added'");
        lines[1] = "Texture 'Changed'";

        Assert.Equal(["Buffer 'Vertices'", "Sampler ''"], thrown.LeakedObjects);
        Assert.Equal("2 GPU object(s) still alive: Buffer 'Vertices', Sampler ''.", thrown.Message);
        // Read-only to a catcher too: the list can't be cast back and changed.
        Assert.IsNotType<string[]>(thrown.LeakedObjects);
        Assert.IsNotType<List<string>>(thrown.LeakedObjects);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)thrown.LeakedObjects)[0] = "Texture 'Changed'");
        Assert.Equal("Buffer 'Vertices'", thrown.LeakedObjects[0]);
    }

    [Fact]
    public void Dispose_KindNames_ComeFromBackendObjectKind() =>
        // The leak lines name kinds as BackendObjectKind members do; pinned so a renamed member is noticed here.
        Assert.Equal(
            ["Buffer", "Texture", "TextureView", "Sampler", "ShaderModule", "BindGroupLayout", "PipelineLayout", "BindGroup", "GraphicsPipeline", "ComputePipeline"],
            Enum.GetNames<BackendObjectKind>());

    /// <summary>
    /// A device on the fake, whose ids rise in creation order, or with <paramref name="fallingIds"/> on the fake behind a
    /// <see cref="ReversedIdBackend"/>, whose ids fall; with the front end's table, to read the ids it holds.
    /// </summary>
    private (GraphicsDevice Device, BackendObjectTable Objects) CreateLeakDevice(bool fallingIds)
    {
        if (!fallingIds)
        {
            GraphicsDevice device = _fixture.Create();
            return (device, _fixture.BackendDevice.Objects);
        }

        var backend = new ReversedIdBackend(_fixture.Backend);
        GraphicsDevice reversed = _fixture.Create(backend);
        return (reversed, Assert.IsType<ReversedIdBackendDevice>(backend.LastDevice).Objects);
    }
}
