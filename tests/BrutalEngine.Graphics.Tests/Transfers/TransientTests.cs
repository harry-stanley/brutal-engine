using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Tests.Device;
using BrutalEngine.TestUtilities;
using Xunit;
using Op = BrutalEngine.TestUtilities.FakeBackendOperation;

namespace BrutalEngine.Graphics.Tests.Transfers;

/// <summary>Transient allocations from the upload ring and their alias handles (docs/design/graphics.md section 6).</summary>
public sealed class TransientTests : IDisposable
{
    // The smallest ring the options accept, so overflow is cheap to reach.
    private const long RingBytes = 64 * 1024;

    // The fake's uniform alignment is 256; storage gets a larger one here, so the two can't be confused.
    private const long UniformAlignment = 256;
    private const long StorageAlignment = 512;

    private const BufferUsage BlockUsage = BufferUsage.Vertex | BufferUsage.Index | BufferUsage.Uniform | BufferUsage.Storage
        | BufferUsage.Indirect | BufferUsage.CopySource;

    private readonly DeviceFixture _fixture = new();
    private readonly MappedBufferBackend _backend;

    public TransientTests()
    {
        GraphicsCapabilities defaults = FakeBackendScript.DefaultCapabilities;
        _fixture.Script.Capabilities = defaults with { Limits = defaults.Limits with { MinStorageBufferOffsetAlignment = StorageAlignment } };
        _backend = new MappedBufferBackend(_fixture.Backend);
    }

    public void Dispose() => _fixture.Dispose();

    public static TheoryData<long, long, TransientUsage, long, int> Usages()
    {
        var rows = new TheoryData<long, long, TransientUsage, long, int>();
        (long, long, TransientUsage, long)[] usages =
        [
            // Uniform limit, storage limit, usage, expected alignment. Storage above uniform:
            (UniformAlignment, StorageAlignment, TransientUsage.Vertex, 16),
            (UniformAlignment, StorageAlignment, TransientUsage.Index, 16),
            (UniformAlignment, StorageAlignment, TransientUsage.Indirect, 16),
            (UniformAlignment, StorageAlignment, TransientUsage.Uniform, UniformAlignment),
            (UniformAlignment, StorageAlignment, TransientUsage.Storage, StorageAlignment),
            (UniformAlignment, StorageAlignment, TransientUsage.Vertex | TransientUsage.Uniform, UniformAlignment),
            (UniformAlignment, StorageAlignment, TransientUsage.Uniform | TransientUsage.Storage, StorageAlignment),

            // Storage below uniform (the RTX 2070 SUPER's limits): the combination takes the larger, uniform one.
            (64, 16, TransientUsage.Uniform, 64),
            (64, 16, TransientUsage.Storage, 16),
            (64, 16, TransientUsage.Uniform | TransientUsage.Storage, 64),

            // Limits below 16: every offset keeps the ring's 16 bytes.
            (4, 4, TransientUsage.Uniform, 16),
            (4, 4, TransientUsage.Storage, 16),
            (4, 4, TransientUsage.Uniform | TransientUsage.Storage, 16),
        ];
        foreach ((long uniform, long storage, TransientUsage usage, long alignment) in usages)
        {
            for (int frames = 1; frames <= 3; frames++)
            {
                rows.Add(uniform, storage, usage, alignment, frames);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(Usages))]
    public void Alignment_PerUsage(long uniformLimit, long storageLimit, TransientUsage usage, long alignment, int framesInFlight)
    {
        GraphicsCapabilities defaults = FakeBackendScript.DefaultCapabilities;
        _fixture.Script.Capabilities = defaults with
        {
            Limits = defaults.Limits with { MinUniformBufferOffsetAlignment = uniformLimit, MinStorageBufferOffsetAlignment = storageLimit },
        };
        GraphicsDevice device = Create(framesInFlight);
        Assert.Equal((uniformLimit, storageLimit), (device.Capabilities.Limits.MinUniformBufferOffsetAlignment, device.Capabilities.Limits.MinStorageBufferOffsetAlignment));

        // One frame more than there are slots: the last reuses slot 0's ring, which must start empty again.
        for (int frame = 0; frame <= framesInFlight; frame++)
        {
            _ = device.BeginFrame(out CommandList commands);
            // A 17-byte allocation first, so the next offset shows the usage's alignment rather than the ring's start. It is
            // larger than the smallest alignment, 16, so a rounding mask of the wrong width lands elsewhere: 17 bytes round
            // up to 32 for 16 and to the alignment itself for the larger ones.
            Assert.True(commands.AllocateTransient(17, TransientUsage.Vertex, out TransientAllocation first).IsSuccess);
            Assert.True(commands.AllocateTransient(40, usage, out TransientAllocation second).IsSuccess);

            Assert.Equal((0L, alignment == 16 ? 32 : alignment), (first.Offset, second.Offset));
            Assert.Equal((17, 40), (first.Data.Length, second.Data.Length));
            Assert.Equal(first.Buffer, second.Buffer);

            // Data is the block's mapped memory at Offset, written until the submit.
            byte fill = (byte)(frame + 1);
            second.Data.Fill(fill);
            ulong block = _fixture.BackendDevice.Objects.GetId(second.Buffer);
            long offset = second.Offset;
            byte[] seen = [];
            _backend.Device!.OnExecute = () => seen = _backend.Device.Read(block, offset, 40);
            _ = device.SubmitFrame(commands);
            Assert.Equal(Enumerable.Repeat(fill, 40), seen);
        }

        // One ring per slot, and nothing overflowed or counted as uploaded.
        Assert.Equal(framesInFlight, _backend.Device!.HostBuffers.Count);
        Assert.Equal((0L, 0L), (device.Statistics.UploadOverflowBytes, device.Statistics.UploadBytesLastFrame));
    }

    [Fact]
    public void Blocks_HaveEveryTransientUsageAndCopySource()
    {
        GraphicsDevice device = Create();
        _ = device.BeginFrame(out CommandList commands);
        Assert.True(commands.AllocateTransient(16, TransientUsage.Uniform, out TransientAllocation ring).IsSuccess);
        Assert.True(commands.AllocateTransient(RingBytes + 1, TransientUsage.Index, out TransientAllocation overflow).IsSuccess);
        ulong[] blocks = [_fixture.BackendDevice.Objects.GetId(ring.Buffer), _fixture.BackendDevice.Objects.GetId(overflow.Buffer)];
        _ = device.SubmitFrame(commands);

        Assert.Equal(_backend.Device!.HostBuffers, blocks);
        Assert.All(blocks, id => Assert.Equal((BlockUsage, BackendMemory.HostUpload), (_backend.Device.Buffers[id].Description.Usage, _backend.Device.Buffers[id].Memory)));
        Assert.Equal([RingBytes, RingBytes + 1], blocks.Select(id => _backend.Device.Buffers[id].Description.Size));
    }

    [Fact]
    public void BetweenFrameBlocks_AreCopySourceOnly()
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 2 * RingBytes, Usage = BufferUsage.CopyDestination }).Value;

        // Between frames a write takes a pending block, or a dedicated block when it is larger than one: neither ever gets
        // an alias, so neither gets the transient usages.
        Assert.True(device.WriteBuffer(buffer, 0, [1]).IsSuccess);
        Assert.True(device.WriteBuffer(buffer, 0, new byte[RingBytes + 16]).IsSuccess);
        RunFrame(device);

        Assert.Equal(2, _backend.Device!.HostBuffers.Count);
        Assert.All(_backend.Device.HostBuffers, id => Assert.Equal((BufferUsage.CopySource, BackendMemory.HostUpload), (_backend.Device.Buffers[id].Description.Usage, _backend.Device.Buffers[id].Memory)));
        Assert.True(device.WaitIdle().IsSuccess);
        device.Destroy(buffer);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AliasAfterLostSubmit_IsStale(int framesInFlight)
    {
        GraphicsDevice device = Create(framesInFlight);
        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex }).Value;
        for (int frame = 0; frame < framesInFlight; frame++)
        {
            RunFrame(device);
        }

        // The submission fails, which loses the device, in a frame with a ring and an overflow allocation.
        _ = device.BeginFrame(out CommandList commands);
        Assert.True(commands.AllocateTransient(16, TransientUsage.Uniform, out TransientAllocation ring).IsSuccess);
        Assert.True(commands.AllocateTransient(RingBytes + 1, TransientUsage.Vertex, out TransientAllocation overflow).IsSuccess);
        BufferHandle[] aliases = [ring.Buffer, overflow.Buffer];
        _fixture.Script.FailNext(Op.Execute, new EngineError(ErrorKind.Unavailable, "Injected loss.", -4));

        Assert.Equal(FrameStatus.DeviceLost, device.SubmitFrame(commands));

        // The closed frame's aliases are stale all the same, and the live objects are still just the buffer.
        foreach (BufferHandle alias in aliases)
        {
            _ = Assert.Throws<StaleHandleException>(() => device.WriteBuffer(alias, 0, [1]));
            _ = Assert.Throws<StaleHandleException>(() => device.Destroy(alias));
        }

        Assert.Equal(1, device.Statistics.LiveObjects);
        device.Destroy(buffer);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AliasAfterFrame_IsStale(int framesInFlight)
    {
        GraphicsDevice device = Create(framesInFlight);
        var aliases = new List<BufferHandle>();
        for (int frame = 0; frame <= framesInFlight; frame++)
        {
            _ = device.BeginFrame(out CommandList commands);
            Assert.True(commands.AllocateTransient(16, TransientUsage.Vertex, out TransientAllocation allocation).IsSuccess);
            BufferHandle alias = allocation.Buffer;

            // Live in its frame: a write is refused for the alias's usage (no CopyDestination), not as a stale handle.
            _ = Assert.Throws<ArgumentException>(() => device.WriteBuffer(alias, 0, [1]));
            Assert.All(aliases, earlier => Assert.Throws<StaleHandleException>(() => device.WriteBuffer(earlier, 0, [1])));
            _ = device.SubmitFrame(commands);

            // Stale from the submit on, between frames and in every later frame.
            _ = Assert.Throws<StaleHandleException>(() => device.WriteBuffer(alias, 0, [1]));
            _ = Assert.Throws<StaleHandleException>(() => device.Destroy(alias));
            aliases.Add(alias);
        }

        Assert.Equal(aliases.Count, aliases.Distinct().Count());
        Assert.Equal(0, device.Statistics.LiveObjects);
    }

    [Fact]
    public void Alias_DestroyThrows_AndIsNotLive()
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex }).Value;
        _ = device.BeginFrame(out CommandList commands);
        Assert.True(commands.AllocateTransient(16, TransientUsage.Uniform, out TransientAllocation ring).IsSuccess);
        Assert.True(commands.AllocateTransient(RingBytes + 1, TransientUsage.Vertex, out TransientAllocation overflow).IsSuccess);
        BufferHandle[] aliases = [ring.Buffer, overflow.Buffer];

        // An alias per block, neither counted as a live object.
        Assert.NotEqual(aliases[0], aliases[1]);
        Assert.Equal(1, device.Statistics.LiveObjects);
        device.Destroy(buffer);
        Assert.Equal(0, device.Statistics.LiveObjects);
        device.ThrowIfLeaks();

        int calls = _fixture.BackendDevice.Calls.Count;
        foreach (BufferHandle alias in aliases)
        {
            ArgumentException refused = Assert.Throws<ArgumentException>(() => device.Destroy(alias));
            Assert.Equal("buffer", refused.ParamName);
            Assert.Contains("transient allocation", refused.Message, StringComparison.Ordinal);

            // The refusal changed nothing: the alias is still live (refused for its usage, not stale).
            _ = Assert.Throws<ArgumentException>(() => device.WriteBuffer(alias, 0, [1]));
        }

        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
        _ = device.SubmitFrame(commands);

        // Only the overflow block goes, once its frame completed; the ring stays for the slot's next frames.
        ulong ringId = _backend.Device!.HostBuffers[0];
        for (int frame = 0; frame < 2; frame++)
        {
            _ = device.BeginFrame(out commands);
            Assert.True(commands.AllocateTransient(16, TransientUsage.Uniform, out _).IsSuccess);
            _ = device.SubmitFrame(commands);
        }

        FakeBackendCall[] destroyed = [.. _fixture.BackendDevice.Calls.Skip(calls).Where(call => call.Operation == Op.Destroy)];
        Assert.DoesNotContain(destroyed, call => call.Id == ringId);
        Assert.Contains(destroyed, call => call.Id == _backend.Device.HostBuffers[1]);
    }

    [Fact]
    public void DisposeWithFrameOpen_DestroysEachBlockOnce()
    {
        GraphicsDevice device = Create();
        _ = device.BeginFrame(out CommandList commands);
        Assert.True(commands.AllocateTransient(16, TransientUsage.Uniform, out TransientAllocation ring).IsSuccess);
        Assert.True(commands.AllocateTransient(RingBytes + 1, TransientUsage.Vertex, out TransientAllocation overflow).IsSuccess);
        ulong[] blocks = [_fixture.BackendDevice.Objects.GetId(ring.Buffer), _fixture.BackendDevice.Objects.GetId(overflow.Buffer)];

        device.Dispose();

        // Each block reached the backend's Destroy exactly once, before the backend was disposed (the fake rejects a
        // second one), and no alias was reported as a leak. The fixture's tracking allocator then sees the mapped memory freed.
        List<FakeBackendCall> calls = [.. _fixture.BackendDevice.Calls];
        int disposed = calls.FindIndex(call => call.Operation == Op.Dispose);
        Assert.All(blocks, id => Assert.Single(calls.Take(disposed), call => call.Operation == Op.Destroy && call.Id == id));
        Assert.DoesNotContain(_fixture.Entries(), entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public void LostDevice_Unavailable()
    {
        GraphicsDevice device = Create();
        _fixture.Script.FailNext(Op.Execute, new EngineError(ErrorKind.Unavailable, "Injected loss.", -4));
        RunFrame(device);
        Assert.Equal(FrameStatus.DeviceLost, device.BeginFrame(out CommandList commands));
        int calls = _fixture.BackendDevice.Calls.Count;

        Result result = commands.AllocateTransient(16, TransientUsage.Vertex, out TransientAllocation allocation);

        Assert.Equal((ErrorKind.Unavailable, "CommandList.AllocateTransient: the device is lost."), (result.Error.Kind, result.Error.Message));
        Assert.Equal((true, 0L, 0), (allocation.Buffer.IsNull, allocation.Offset, allocation.Data.Length));
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
        Assert.Equal(0, device.Statistics.UploadOverflowBytes);
        _ = device.SubmitFrame(commands);
    }

    public static TheoryData<string> OverflowCases() => ["fits", "overflows", "overflow block fails", "ring fails"];

    [Theory]
    [MemberData(nameof(OverflowCases))]
    public void Overflow_CountedOnlyOnSuccess(string kind)
    {
        GraphicsDevice device = Create();
        _ = device.BeginFrame(out CommandList commands);
        BufferHandle ringAlias = default;
        if (kind != "ring fails")
        {
            // 3 bytes first: the uniform allocation then starts at 256, so RingBytes - 256 bytes end exactly at the
            // ring's end and one byte more doesn't fit.
            Assert.True(commands.AllocateTransient(3, TransientUsage.Vertex, out TransientAllocation first).IsSuccess);
            ringAlias = first.Buffer;
        }

        long size = kind switch
        {
            "fits" => RingBytes - UniformAlignment,
            "ring fails" => 16,
            _ => RingBytes - UniformAlignment + 1,
        };
        bool fails = kind.EndsWith("fails", StringComparison.Ordinal);
        var injected = new EngineError(ErrorKind.OutOfMemory, "Injected block failure.", -2);
        if (fails)
        {
            _fixture.Script.FailNext(Op.CreateBuffer, injected);
        }

        int created = _backend.Device!.HostBuffers.Count;
        Result result = commands.AllocateTransient(size, TransientUsage.Uniform, out TransientAllocation allocation);

        if (fails)
        {
            // Nothing reserved, counted or logged.
            Assert.Equal(injected, result.Error);
            Assert.Equal((true, 0), (allocation.Buffer.IsNull, allocation.Data.Length));
            Assert.Equal(created, _backend.Device.HostBuffers.Count);
            Assert.Equal(0, device.Statistics.UploadOverflowBytes);
            Assert.Empty(Warnings());

            // Tried again, the same allocation succeeds, and only then counts.
            Assert.True(commands.AllocateTransient(size, TransientUsage.Uniform, out allocation).IsSuccess);
            Assert.Equal(kind == "ring fails" ? 0 : size, device.Statistics.UploadOverflowBytes);
        }
        else if (kind == "fits")
        {
            Assert.True(result.IsSuccess);
            Assert.Equal((ringAlias, UniformAlignment, (int)size), (allocation.Buffer, allocation.Offset, allocation.Data.Length));
            Assert.Equal(created, _backend.Device.HostBuffers.Count);
            Assert.Equal(0, device.Statistics.UploadOverflowBytes);
            Assert.Empty(Warnings());
        }
        else
        {
            // A dedicated block of exactly the size, with an alias of its own; counted, and logged once.
            Assert.True(result.IsSuccess);
            ulong block = _backend.Device.HostBuffers[^1];
            Assert.Equal((created + 1, size), (_backend.Device.HostBuffers.Count, _backend.Device.Buffers[block].Description.Size));
            Assert.Equal((block, 0L, (int)size), (_fixture.BackendDevice.Objects.GetId(allocation.Buffer), allocation.Offset, allocation.Data.Length));
            Assert.NotEqual(ringAlias, allocation.Buffer);
            Assert.Equal(size, device.Statistics.UploadOverflowBytes);
            Assert.True(commands.AllocateTransient(RingBytes, TransientUsage.Vertex, out _).IsSuccess);
            Assert.Equal(size + RingBytes, device.Statistics.UploadOverflowBytes);
            string warning = Assert.Single(Warnings());
            Assert.StartsWith("CommandList.AllocateTransient: ", warning, StringComparison.Ordinal);
        }

        _ = device.SubmitFrame(commands);
        Assert.Equal(0, device.Statistics.UploadBytesLastFrame);
    }

    [Fact]
    public void Misuse_ThrowsBeforeReserving()
    {
        GraphicsDevice device = Create();
        _ = device.BeginFrame(out CommandList stale);
        _ = device.SubmitFrame(stale);
        _ = device.BeginFrame(out CommandList commands);
        int calls = _fixture.BackendDevice.Calls.Count;

        _ = Assert.Throws<InvalidOperationException>(() => default(CommandList).AllocateTransient(16, TransientUsage.Vertex, out _));
        _ = Assert.Throws<InvalidOperationException>(() => stale.AllocateTransient(16, TransientUsage.Vertex, out _));
        foreach (long byteCount in new[] { 0L, -1L, int.MaxValue + 1L, long.MaxValue })
        {
            ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(() => commands.AllocateTransient(byteCount, TransientUsage.Vertex, out _));
            Assert.Equal("byteCount", e.ParamName);
        }

        foreach (TransientUsage usage in new[] { (TransientUsage)32, (TransientUsage)(-1) })
        {
            ArgumentOutOfRangeException e = Assert.Throws<ArgumentOutOfRangeException>(() => commands.AllocateTransient(16, usage, out _));
            Assert.Equal("usage", e.ParamName);
        }

        Assert.Equal("usage", Assert.Throws<ArgumentException>(() => commands.AllocateTransient(16, 0, out _)).ParamName);
        Exception? offThread = null;
        var thread = new Thread(() => offThread = Record.Exception(() => commands.AllocateTransient(16, TransientUsage.Vertex, out _)));
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        _ = Assert.IsType<WrongThreadException>(offThread);
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);

        // int.MaxValue bytes pass the checks: the dedicated block is asked for (and here refused, so nothing is mapped).
        Assert.True(commands.AllocateTransient(16, TransientUsage.Vertex, out _).IsSuccess);
        _fixture.Script.FailNext(Op.CreateBuffer, new EngineError(ErrorKind.OutOfMemory, "Injected.", -2));
        Assert.Equal(ErrorKind.OutOfMemory, commands.AllocateTransient(int.MaxValue, TransientUsage.Vertex, out _).Error.Kind);
        _ = device.SubmitFrame(commands);

        device.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => commands.AllocateTransient(16, TransientUsage.Vertex, out _));
    }

    private static void RunFrame(GraphicsDevice device)
    {
        _ = device.BeginFrame(out CommandList commands);
        _ = device.SubmitFrame(commands);
    }

    private GraphicsDevice Create(int framesInFlight = 2) =>
        _fixture.Create(_backend, _fixture.Options() with { FramesInFlight = framesInFlight, UploadRingBytesPerFrame = RingBytes });

    private List<string> Warnings() =>
        [.. _fixture.Entries().Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message)];
}
