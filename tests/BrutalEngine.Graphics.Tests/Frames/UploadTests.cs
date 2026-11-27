using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Internal;
using BrutalEngine.Graphics.Recording;
using BrutalEngine.Graphics.Tests.Device;
using BrutalEngine.TestUtilities;
using CsCheck;
using Xunit;
using Op = BrutalEngine.TestUtilities.FakeBackendOperation;

namespace BrutalEngine.Graphics.Tests.Frames;

/// <summary>Buffer and texture writes through the upload ring and pending upload blocks (docs/design/graphics.md section 6).</summary>
public sealed class UploadTests : IDisposable
{
    // The smallest ring the options accept, so overflow is cheap to reach.
    private const long RingBytes = 64 * 1024;

    private static readonly BufferDescription _target = new() { Size = 256 * 1024, Usage = BufferUsage.CopyDestination | BufferUsage.Vertex };

    private readonly DeviceFixture _fixture = new();
    private readonly GpuSimulatorBackend _gpu;

    public UploadTests() => _gpu = new GpuSimulatorBackend(_fixture.Backend);

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Write_CopiedAtCallAndOrderedFirst()
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        ulong bufferId = _fixture.BackendDevice.Objects.GetId(buffer);
        byte[] pending = [1, 2, 3, 4, 5];
        byte[] inFrame = [9, 8, 7];

        // One write before the frame (a pending block), one inside it (the ring); the caller reuses its arrays at once.
        Assert.True(device.WriteBuffer(buffer, 100, pending).IsSuccess);
        Array.Fill(pending, (byte)0xFF);
        _ = device.BeginFrame(out CommandList commands);
        Assert.True(device.WriteBuffer(buffer, 7, inFrame).IsSuccess);
        Array.Fill(inFrame, (byte)0xFF);
        _ = device.SubmitFrame(commands);

        // The plan is the two copies, in call order, of the bytes as they were at each call.
        StagedCopy[] plan = _gpu.Device!.Plans[^1];
        Assert.Equal(2, plan.Length);
        Assert.All(plan, copy => Assert.Equal(CommandKind.CopyBufferToBuffer, copy.Kind));
        Assert.Equal((bufferId, 100L, 5L), (plan[0].Buffer.Destination, plan[0].Buffer.DestinationOffset, plan[0].Buffer.Size));
        Assert.Equal((bufferId, 7L, 3L), (plan[1].Buffer.Destination, plan[1].Buffer.DestinationOffset, plan[1].Buffer.Size));
        Assert.Equal([1, 2, 3, 4, 5], plan[0].Staged);
        Assert.Equal([9, 8, 7], plan[1].Staged);
        Assert.NotEqual(plan[0].Buffer.Source, plan[1].Buffer.Source);
        Assert.Equal(8, device.Statistics.UploadBytesLastFrame);

        // The copies landed once: the next frame plans nothing and uploads nothing.
        RunFrame(device);
        Assert.Empty(_gpu.Device.Plans[^1]);
        Assert.Equal(0, device.Statistics.UploadBytesLastFrame);
        Assert.Equal(0, device.Statistics.UploadOverflowBytes);
    }

    [Fact]
    public void Planner_UploadsBeforeRecordedCommands()
    {
        var allocator = new TrackingAllocator(new HeapAllocator(), "planner");
        using (var uploads = new CommandWriter(allocator))
        using (var recorded = new CommandWriter(allocator))
        using (var planner = new FramePlanner(allocator))
        {
            recorded.Append(CommandKind.Draw, new DrawCommand { Arguments = new DrawArguments(3) });
            Assert.True(planner.Plan(1, 5, uploads, recorded).Commands.SequenceEqual(recorded.Written));
            uploads.Append(CommandKind.CopyBufferToBuffer, new CopyBufferToBufferCommand { Source = 1, Destination = 2, Size = 4 });

            FramePlan plan = planner.Plan(1, 5, uploads, recorded);

            Assert.Equal((1, 5L), (plan.FrameSlot, plan.FrameNumber));
            var reader = new CommandReader(plan.Commands);
            Assert.True(reader.MoveNext());
            Assert.Equal(CommandKind.CopyBufferToBuffer, reader.Kind);
            Assert.True(reader.MoveNext());
            Assert.Equal((CommandKind.Draw, 3), (reader.Kind, reader.Payload<DrawCommand>().Arguments.VertexCount));
            Assert.False(reader.MoveNext());

            // Only uploads: the plan is the upload stream itself.
            recorded.Reset();
            Assert.True(planner.Plan(0, 6, uploads, recorded).Commands.SequenceEqual(uploads.Written));
        }

        allocator.ThrowIfLeaks();
    }

    [Fact]
    public void WriteBetweenFrames_UsesPendingBlock()
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        RunFrame(device);

        // Before frame 1: a new pending block.
        Assert.True(device.WriteBuffer(buffer, 0, [1]).IsSuccess);
        ulong first = _gpu.Device!.HostBuffers[^1];
        Assert.Equal(RingBytes, _gpu.Device.Sizes[first]);
        RunFrame(device);
        Assert.Equal(first, _gpu.Device.Plans[^1].Single().Buffer.Source);

        // Before frame 2: frame 1 may still be running, so its block isn't reused.
        Assert.True(device.WriteBuffer(buffer, 0, [2]).IsSuccess);
        ulong second = _gpu.Device.HostBuffers[^1];
        Assert.NotEqual(first, second);
        RunFrame(device);
        Assert.Equal(second, _gpu.Device.Plans[^1].Single().Buffer.Source);

        // Frame 3 waits for slot 1, so frame 1 completed and its block came back: before frame 4 it's reused.
        RunFrame(device);
        int created = _gpu.Device.HostBuffers.Count;
        Assert.True(device.WriteBuffer(buffer, 0, [3]).IsSuccess);
        RunFrame(device);
        Assert.Equal(created, _gpu.Device.HostBuffers.Count);
        Assert.Equal(first, _gpu.Device.Plans[^1].Single().Buffer.Source);
        Assert.Equal([3], _gpu.Device.Plans[^1].Single().Staged);
    }

    [Fact]
    public void RingSlice_ReusedOnceItsSlotCompleted()
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        byte[] data = new byte[40 * 1024];

        // Each frame fills most of its slot's 64 KiB slice: only a slice freed when its slot began again avoids overflow.
        for (int frame = 0; frame < 6; frame++)
        {
            _ = device.BeginFrame(out CommandList commands);
            data[0] = (byte)frame;
            Assert.True(device.WriteBuffer(buffer, 0, data).IsSuccess);
            _ = device.SubmitFrame(commands);
        }

        Assert.Equal(0, device.Statistics.UploadOverflowBytes);
        Assert.Equal(2, _gpu.Device!.HostBuffers.Count);
        Assert.Equal(_gpu.Device.Plans[0].Single().Buffer.Source, _gpu.Device.Plans[4].Single().Buffer.Source);
        Assert.Equal((0L, (byte)5), (_gpu.Device.Plans[4].Single().Buffer.SourceOffset, _gpu.Device.Plans[5].Single().Staged[0]));
    }

    [Fact]
    public void WaitIdle_ReturnsPendingBlocks()
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        RunFrame(device);
        Assert.True(device.WriteBuffer(buffer, 0, [1]).IsSuccess);
        RunFrame(device);
        Assert.True(device.WaitIdle().IsSuccess);
        int created = _gpu.Device!.HostBuffers.Count;

        Assert.True(device.WriteBuffer(buffer, 0, [2]).IsSuccess);

        Assert.Equal(created, _gpu.Device.HostBuffers.Count);
    }

    [Fact]
    public void WaitIdleInFrame_KeepsTheOpenFramesPendingBlocks()
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        Assert.True(device.WriteBuffer(buffer, 0, [1]).IsSuccess);
        ulong first = _gpu.Device!.HostBuffers[^1];

        // Frame 0 takes the pending block; waiting for idle inside it must not free a block the frame hasn't copied yet.
        _ = device.BeginFrame(out CommandList commands);
        Assert.True(device.WaitIdle().IsSuccess);
        _ = device.SubmitFrame(commands);
        Assert.Equal((first, (byte)1), (_gpu.Device.Plans[^1].Single().Buffer.Source, _gpu.Device.Plans[^1].Single().Staged[0]));

        // Frame 0 may still be running, so the next pending write gets a new block.
        int created = _gpu.Device.HostBuffers.Count;
        Assert.True(device.WriteBuffer(buffer, 0, [2]).IsSuccess);
        Assert.Equal(created + 1, _gpu.Device.HostBuffers.Count);
        RunFrame(device);
        Assert.Equal((_gpu.Device.HostBuffers[^1], (byte)2), (_gpu.Device.Plans[^1].Single().Buffer.Source, _gpu.Device.Plans[^1].Single().Staged[0]));
    }

    [Theory]
    [InlineData(RingBytes, false)]
    [InlineData(RingBytes + 1, true)]
    public void WriteBetweenFrames_LargerThanRingOnly_Overflows(long size, bool overflows)
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        RunFrame(device);

        // A pending block holds one ring's worth, so a write of exactly that size still fits one.
        Assert.True(device.WriteBuffer(buffer, 0, new byte[size]).IsSuccess);
        ulong block = _gpu.Device!.HostBuffers[^1];
        RunFrame(device);

        Assert.Equal(overflows ? size : RingBytes, _gpu.Device.Sizes[block]);
        Assert.Equal(overflows ? size : 0, device.Statistics.UploadOverflowBytes);
        Assert.Equal(overflows ? 1 : 0, Warnings().Count);
        Assert.Equal(block, _gpu.Device.Plans[^1].Single().Buffer.Source);
    }

    [Fact]
    public void Overflow_CountedAndLoggedOnce()
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        byte[] data = new byte[40 * 1024];

        // Frame 0: the first write fits the ring, the next two don't and get a dedicated block each.
        _ = device.BeginFrame(out CommandList commands);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(device.WriteBuffer(buffer, 0, data).IsSuccess);
        }

        ulong[] dedicated = [.. _gpu.Device!.HostBuffers.TakeLast(2)];
        Assert.All(dedicated, id => Assert.Equal(data.Length, _gpu.Device.Sizes[id]));
        _ = device.SubmitFrame(commands);
        Assert.Equal(2L * data.Length, device.Statistics.UploadOverflowBytes);
        Assert.Equal(3L * data.Length, device.Statistics.UploadBytesLastFrame);
        Assert.Single(Warnings());

        // Frame 1 overflows again (a warning of its own); between frames a write larger than a block overflows too.
        _ = device.BeginFrame(out commands);
        Assert.True(device.WriteBuffer(buffer, 0, data).IsSuccess);
        Assert.True(device.WriteBuffer(buffer, 0, data).IsSuccess);
        _ = device.SubmitFrame(commands);
        Assert.Equal(2, Warnings().Count);
        Assert.True(device.WriteBuffer(buffer, 0, new byte[RingBytes + 16]).IsSuccess);
        ulong large = _gpu.Device.HostBuffers[^1];
        Assert.Equal(RingBytes + 16, _gpu.Device.Sizes[large]);
        Assert.Equal((3L * data.Length) + RingBytes + 16, device.Statistics.UploadOverflowBytes);
        Assert.Contains("Raise GraphicsDeviceOptions.UploadRingBytesPerFrame", Warnings()[0], StringComparison.Ordinal);

        // The dedicated blocks are destroyed once their frame completed, not before.
        Assert.DoesNotContain(_fixture.BackendDevice.Calls, call => call.Operation == Op.Destroy);
        int start = _fixture.BackendDevice.Calls.Count;
        RunFrame(device);
        FakeBackendCall[] frame2 = [.. _fixture.BackendDevice.Calls.Skip(start)];
        Assert.Equal(dedicated, frame2.Where(call => call.Operation == Op.Destroy).Select(call => call.Id));
        start = _fixture.BackendDevice.Calls.Count;
        RunFrame(device);
        RunFrame(device);
        Assert.Contains(_fixture.BackendDevice.Calls.Skip(start), call => call.Operation == Op.Destroy && call.Id == large);
        Assert.Equal(3, Warnings().Count);
    }

    [Fact]
    public void Overflow_CopyCannotBeRecorded_NotCounted()
    {
        var failing = new FailingAllocator(_fixture.Allocator);
        GraphicsDevice device = _fixture.Create(_gpu, _fixture.Options() with { UploadRingBytesPerFrame = RingBytes, Allocator = failing });
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        int record = (int)CommandRecordHeader.AlignUp(CommandRecordHeader.TrailingOffset(Unsafe.SizeOf<CopyBufferToBufferCommand>()));
        int records = CommandWriter.DefaultInitialCapacity / record;

        // Fill the frame's upload stream with ring writes, the last one taking the rest of the ring, so the next write
        // overflows and its copy needs the stream to grow.
        _ = device.BeginFrame(out CommandList commands);
        Assert.True(device.WriteBuffer(buffer, 0, [1]).IsSuccess);
        int allocations = failing.Allocations;
        for (int i = 1; i < records - 1; i++)
        {
            Assert.True(device.WriteBuffer(buffer, 0, [1]).IsSuccess);
        }

        Assert.True(device.WriteBuffer(buffer, 0, new byte[RingBytes - ((records - 1) * UploadRing.Alignment)]).IsSuccess);
        Assert.Equal(allocations, failing.Allocations);
        int created = _gpu.Device!.HostBuffers.Count;
        // The fake maps the dedicated block through the device's allocator: that succeeds, the stream's growth fails.
        failing.FailAfter = 1;

        Assert.ThrowsAny<OutOfMemoryException>(() => device.WriteBuffer(buffer, 0, [2]));

        // The dedicated block was made (it goes once the frame completes), but no copy was recorded: nothing counted or logged.
        Assert.Equal((created + 1, allocations + 1), (_gpu.Device.HostBuffers.Count, failing.Allocations));
        Assert.Equal(0, device.Statistics.UploadOverflowBytes);
        Assert.Empty(Warnings());

        // The write succeeds when tried again, and only then counts and logs.
        Assert.True(device.WriteBuffer(buffer, 0, [2]).IsSuccess);
        _ = device.SubmitFrame(commands);
        Assert.Equal(records + 1, _gpu.Device.Plans[^1].Length);
        Assert.Equal([2], _gpu.Device.Plans[^1][^1].Staged);
        Assert.Equal(1, device.Statistics.UploadOverflowBytes);
        Assert.Single(Warnings());
    }

    [Fact]
    public void WriteTexture_Overflow_CountsStagedBytes()
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        TextureHandle texture = device.CreateTexture(new TextureDescription
        {
            Format = TextureFormat.Rgba8Unorm,
            Width = 16,
            Height = 16,
            Usage = TextureUsage.CopyDestination | TextureUsage.Sampled,
        }).Value;
        _ = device.BeginFrame(out CommandList commands);
        Assert.True(device.WriteBuffer(buffer, 0, new byte[RingBytes]).IsSuccess);

        // 3 rows of 5 Rgba8 texels (20 bytes) staged 256 bytes apart: 532 bytes, which overflow the full ring.
        Assert.True(device.WriteTexture(new TextureWrite { Target = new(texture), Region = new(0, 0, 5, 3) }, new byte[3 * 20]).IsSuccess);
        _ = device.SubmitFrame(commands);

        // The overflow counts the staged bytes, the dedicated block's size; the frame's upload bytes count the texels.
        const long staged = (256 * 2) + 20;
        Assert.Equal(staged, _gpu.Device!.Sizes[_gpu.Device.HostBuffers[^1]]);
        Assert.Equal(staged, device.Statistics.UploadOverflowBytes);
        Assert.Equal(RingBytes + (3 * 20), device.Statistics.UploadBytesLastFrame);
    }

    public static TheoryData<int, int, int> RowPitches() => new()
    {
        // Width (Rgba8 texels), source BytesPerRow (0: tight), expected staged pitch.
        { 5, 24, 256 },
        { 5, 0, 256 },
        { 64, 0, 256 },
        { 65, 300, 512 },
    };

    [Theory]
    [MemberData(nameof(RowPitches))]
    public void WriteTexture_RepacksTo256(int width, int bytesPerRow, int pitch)
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        TextureHandle texture = device.CreateTexture(new TextureDescription
        {
            Format = TextureFormat.Rgba8Unorm,
            Width = 256,
            Height = 64,
            ArrayLayers = 2,
            MipLevels = 2,
            Usage = TextureUsage.CopyDestination | TextureUsage.Sampled,
        }).Value;
        int rowBytes = width * 4;
        int sourcePitch = bytesPerRow == 0 ? rowBytes : bytesPerRow;
        byte[] data = new byte[(sourcePitch * 2) + rowBytes];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i % sourcePitch < rowBytes ? 1 + (i % 251) : 0xEE);
        }

        var write = new TextureWrite { Target = new(texture, MipLevel: 1, ArrayLayer: 1), Region = new(3, 2, width, 3), BytesPerRow = bytesPerRow };
        _ = device.BeginFrame(out CommandList commands);
        // A 3-byte write first, so the texels must start at the next 16-byte boundary of the same ring, not at 3.
        Assert.True(device.WriteBuffer(buffer, 0, [1, 2, 3]).IsSuccess);
        Assert.True(device.WriteTexture(write, data).IsSuccess);
        _ = device.SubmitFrame(commands);

        StagedCopy[] plan = _gpu.Device!.Plans[^1];
        Assert.Equal(2, plan.Length);
        StagedCopy copy = plan[1];
        Assert.Equal(CommandKind.CopyBufferToTexture, copy.Kind);
        Assert.Equal(plan[0].Buffer.Source, copy.Texture.Buffer);
        CopyBufferTextureCommand command = copy.Texture;
        Assert.Equal(
            (_fixture.BackendDevice.Objects.GetId(texture), 1, 1, TextureAspect.Color, write.Region, pitch),
            (command.Texture, command.MipLevel, command.ArrayLayer, command.Aspect, command.Region, command.BytesPerRow));
        Assert.Equal(16, command.BufferOffset);
        for (int row = 0; row < 3; row++)
        {
            Assert.Equal(data.AsSpan(row * sourcePitch, rowBytes), copy.Staged.AsSpan(row * pitch, rowBytes));
        }

        Assert.Equal(3 + (3L * rowBytes), device.Statistics.UploadBytesLastFrame);
    }

    public static TheoryData<string> FailingBlocks() => ["ring", "pending block", "overflow block"];

    [Theory]
    [MemberData(nameof(FailingBlocks))]
    public void AllocationFails_WritesNothing(string block)
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        bool inFrame = block != "pending block";
        _ = device.BeginFrame(out CommandList commands);
        if (block == "overflow block")
        {
            Assert.True(device.WriteBuffer(buffer, 0, new byte[RingBytes]).IsSuccess);
        }
        else if (!inFrame)
        {
            _ = device.SubmitFrame(commands);
        }

        int calls = _fixture.BackendDevice.Calls.Count;
        var error = new EngineError(ErrorKind.OutOfMemory, "Injected staging failure.", -2);
        _fixture.Script.FailNext(Op.CreateBuffer, error);

        Result result = device.WriteBuffer(buffer, 0, [1, 2, 3]);

        Assert.Equal(error, result.Error);
        Assert.Equal(Op.CreateBuffer, _fixture.BackendDevice.Calls.Skip(calls).Single().Operation);
        if (!inFrame)
        {
            _ = device.BeginFrame(out commands);
        }

        _ = device.SubmitFrame(commands);
        // Nothing of the failed write was planned or counted; the earlier ring write is the only copy.
        Assert.Equal(block == "overflow block" ? 1 : 0, _gpu.Device!.Plans[^1].Length);
        Assert.Equal(0, device.Statistics.UploadOverflowBytes);
        Assert.Equal(block == "overflow block" ? RingBytes : 0, device.Statistics.UploadBytesLastFrame);

        // The next write gets its block.
        _ = device.BeginFrame(out commands);
        Assert.True(device.WriteBuffer(buffer, 0, [4]).IsSuccess);
        _ = device.SubmitFrame(commands);
        Assert.Equal([4], _gpu.Device.Plans[^1].Single().Staged);
    }

    [Fact]
    public void Write_LostDevice_UnavailableAndStagesNothing()
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        _fixture.Script.FailNext(Op.Execute, new EngineError(ErrorKind.Unavailable, "Injected loss.", -4));
        RunFrame(device);
        int calls = _fixture.BackendDevice.Calls.Count;

        Result result = device.WriteBuffer(buffer, 0, [1]);

        Assert.Equal((ErrorKind.Unavailable, "GraphicsDevice.WriteBuffer: the device is lost."), (result.Error.Kind, result.Error.Message));
        Assert.Equal(calls, _fixture.BackendDevice.Calls.Count);
    }

    [Fact]
    public void Write_Misuse_ThrowsBeforeStaging()
    {
        GraphicsDevice device = Create();
        BufferHandle vertexOnly = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.Vertex }).Value;
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        TextureHandle sampled = device.CreateTexture(new TextureDescription { Format = TextureFormat.Rgba8Unorm, Width = 4, Height = 4, Usage = TextureUsage.Sampled }).Value;
        int calls = _fixture.BackendDevice.Calls.Count;

        _ = Assert.Throws<ArgumentException>(() => device.WriteBuffer(vertexOnly, 0, [1]));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => device.WriteBuffer(buffer, _target.Size, [1]));
        _ = Assert.Throws<ArgumentException>(() => device.WriteBuffer(default, 0, [1]));
        _ = Assert.Throws<ArgumentException>(() => device.WriteTexture(new TextureWrite { Target = new(sampled), Region = new(0, 0, 1, 1) }, [1, 2, 3, 4]));
        device.Destroy(buffer);
        _ = Assert.Throws<StaleHandleException>(() => device.WriteBuffer(buffer, 0, [1]));

        Assert.DoesNotContain(_fixture.BackendDevice.Calls.Skip(calls), call => call.Operation == Op.CreateBuffer);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void DestroyAfterPendingWrite_WaitsForTheFrameThatCopies(int framesInFlight)
    {
        GraphicsDevice device = Create(framesInFlight);
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        ulong id = _fixture.BackendDevice.Objects.GetId(buffer);
        RunFrame(device);

        // Frame 1 copies the write. Destroyed between frames, the buffer would wait on frame 0's queue, which a slot
        // retires before frame 1 has completed.
        Assert.True(device.WriteBuffer(buffer, 0, [1]).IsSuccess);
        device.Destroy(buffer);
        int destroyedAt = -1;
        for (int frame = 1; frame <= 1 + framesInFlight; frame++)
        {
            int start = _fixture.BackendDevice.Calls.Count;
            RunFrame(device);
            if (_fixture.BackendDevice.Calls.Skip(start).Any(call => call.Operation == Op.Destroy && call.Id == id))
            {
                destroyedAt = frame;
            }
        }

        Assert.Equal(id, _gpu.Device!.Plans[1].Single().Buffer.Destination);
        // Frame 1 + FramesInFlight is the first frame that waits for frame 1.
        Assert.Equal(1 + framesInFlight, destroyedAt);
    }

    [Fact]
    public void Dispose_DestroysEveryStagingBuffer()
    {
        GraphicsDevice device = Create();
        BufferHandle buffer = device.CreateBuffer(_target).Value;
        _ = device.BeginFrame(out CommandList commands);
        Assert.True(device.WriteBuffer(buffer, 0, [1]).IsSuccess);
        _ = device.SubmitFrame(commands);
        Assert.True(device.WriteBuffer(buffer, 0, [2]).IsSuccess);
        RunFrame(device);
        Assert.True(device.WriteBuffer(buffer, 0, [3]).IsSuccess);
        ulong[] ids = [_fixture.BackendDevice.Objects.GetId(buffer)];
        device.Destroy(buffer);

        device.Dispose();

        // The rings, the in-flight and the pending block, and the deferred buffer all reached the backend before it was
        // disposed, each once (the fake rejects a second Destroy).
        List<FakeBackendCall> calls = [.. _fixture.BackendDevice.Calls];
        int disposed = calls.FindIndex(call => call.Operation == Op.Dispose);
        HashSet<ulong> destroyed = [.. calls.Take(disposed).Where(call => call.Operation == Op.Destroy).Select(call => call.Id)];
        Assert.Equal(3, _gpu.Device!.HostBuffers.Count);
        Assert.Subset(destroyed, new HashSet<ulong>([.. _gpu.Device.HostBuffers, ids[0]]));
    }

    [Fact]
    public void RandomWrites_MatchReferenceModel()
    {
        Gen<(bool InFrame, int Buffer, int Offset, byte[] Data)> write =
            Gen.Select(Gen.Bool, Gen.Int[0, 1], Gen.Int[0, 1024], Gen.Byte.Array[1, 80 * 1024], (f, b, o, d) => (f, b, o, d));
        Gen.Select(write.Array[1, 24], Gen.Int[1, 3], (writes, frames) => (writes, frames)).Sample(
            sample =>
            {
                using var fixture = new DeviceFixture();
                var gpu = new GpuSimulatorBackend(fixture.Backend);
                GraphicsDevice device = fixture.Create(gpu, fixture.Options() with { FramesInFlight = sample.frames, UploadRingBytesPerFrame = RingBytes });
                BufferHandle[] buffers = [device.CreateBuffer(_target).Value, device.CreateBuffer(_target).Value];
                byte[][] expected = [new byte[_target.Size], new byte[_target.Size]];
                CommandList commands = default;
                bool open = false;
                foreach ((bool inFrame, int b, int offset, byte[] data) in sample.writes)
                {
                    if (inFrame != open)
                    {
                        _ = open ? device.SubmitFrame(commands) : device.BeginFrame(out commands);
                        open = inFrame;
                    }

                    // The caller's copy is cleared after the call: the device must have copied it already.
                    byte[] caller = (byte[])data.Clone();
                    if (!device.WriteBuffer(buffers[b], offset, caller).IsSuccess)
                    {
                        return false;
                    }

                    data.CopyTo(expected[b], offset);
                    Array.Clear(caller);
                }

                if (open)
                {
                    _ = device.SubmitFrame(commands);
                }

                RunFrame(device);
                bool ok = device.WaitIdle().IsSuccess
                    && gpu.Device!.Contents(fixture.BackendDevice.Objects.GetId(buffers[0])).SequenceEqual(expected[0])
                    && gpu.Device.Contents(fixture.BackendDevice.Objects.GetId(buffers[1])).SequenceEqual(expected[1]);
                device.Destroy(buffers[0]);
                device.Destroy(buffers[1]);
                return ok;
            },
            iter: 60,
            threads: 1);
    }

    private static void RunFrame(GraphicsDevice device)
    {
        _ = device.BeginFrame(out CommandList commands);
        _ = device.SubmitFrame(commands);
    }

    private GraphicsDevice Create(int framesInFlight = 2)
    {
        return _fixture.Create(_gpu, _fixture.Options() with { FramesInFlight = framesInFlight, UploadRingBytesPerFrame = RingBytes });
    }

    private List<string> Warnings() =>
        [.. _fixture.Entries().Where(entry => entry.Level == LogLevel.Warning).Select(entry => entry.Message)];

    /// <summary>One copy of an executed plan, with the staged bytes it reads as they were at <c>Execute</c>.</summary>
    internal sealed record StagedCopy(CommandKind Kind, CopyBufferToBufferCommand Buffer, CopyBufferTextureCommand Texture, byte[] Staged);

    /// <summary>The fake backend, with each device wrapped in a <see cref="GpuSimulator"/>.</summary>
    private sealed class GpuSimulatorBackend(FakeGraphicsBackend inner) : IGraphicsBackend
    {
        public GpuSimulator? Device { get; private set; }

        public string Name => inner.Name;

        public Result<IReadOnlyList<GraphicsAdapterInfo>> EnumerateAdapters(BackendDeviceRequest request) => inner.EnumerateAdapters(request);

        public Result<IBackendDevice> CreateDevice(BackendDeviceRequest request, int adapterIndex, BackendObjectTable objects)
        {
            Result<IBackendDevice> created = inner.CreateDevice(request, adapterIndex, objects);
            if (!created.TryGetValue(out IBackendDevice? device))
            {
                return created;
            }

            Device = new GpuSimulator(device);
            return new Result<IBackendDevice>(Device);
        }
    }

    /// <summary>
    /// Wraps a fake backend device and plays the GPU: it decodes each executed plan's copies (keeping the staged bytes as
    /// they were then, for assertions) and applies the buffer copies to its own model of each buffer when the frame
    /// completes (the slot's next wait, or a wait for idle), reading the staging memory at that moment as a GPU might.
    /// </summary>
    private sealed unsafe class GpuSimulator(IBackendDevice inner) : IBackendDevice
    {
        private readonly Dictionary<ulong, nint> _mapped = [];
        private readonly Dictionary<ulong, byte[]> _contents = [];
        // Each executed frame's buffer copies, oldest first, with its slot, until the frame completes.
        private readonly List<(int Slot, List<CopyBufferToBufferCommand> Copies)> _running = [];

        /// <summary>Every host-visible (staging) buffer created, in order.</summary>
        public List<ulong> HostBuffers { get; } = [];

        /// <summary>Every buffer's size.</summary>
        public Dictionary<ulong, long> Sizes { get; } = [];

        /// <summary>Each executed plan's copies, in order.</summary>
        public List<StagedCopy[]> Plans { get; } = [];

        public GraphicsAdapterInfo Adapter => inner.Adapter;

        public GraphicsCapabilities Capabilities => inner.Capabilities;

        public long ValidationErrors => inner.ValidationErrors;

        public long ValidationWarnings => inner.ValidationWarnings;

        /// <summary>The model of a buffer's contents after every completed frame's copies.</summary>
        public byte[] Contents(ulong buffer) => _contents[buffer];

        public Result<BackendBuffer> CreateBuffer(in BufferDescription description, BackendMemory memory)
        {
            Result<BackendBuffer> created = inner.CreateBuffer(description, memory);
            if (created.TryGetValue(out BackendBuffer buffer))
            {
                Sizes[buffer.Id] = description.Size;
                _contents[buffer.Id] = new byte[description.Size];
                if (buffer.MappedPointer != 0)
                {
                    _mapped[buffer.Id] = buffer.MappedPointer;
                    HostBuffers.Add(buffer.Id);
                }
            }

            return created;
        }

        public Result Execute(in FramePlan plan)
        {
            var copies = new List<StagedCopy>();
            var buffers = new List<CopyBufferToBufferCommand>();
            var reader = new CommandReader(plan.Commands);
            while (reader.MoveNext())
            {
                if (reader.Kind == CommandKind.CopyBufferToBuffer)
                {
                    CopyBufferToBufferCommand copy = reader.Payload<CopyBufferToBufferCommand>();
                    copies.Add(new StagedCopy(reader.Kind, copy, default, Read(copy.Source, copy.SourceOffset, copy.Size)));
                    buffers.Add(copy);
                }
                else
                {
                    CopyBufferTextureCommand copy = reader.Payload<CopyBufferTextureCommand>();
                    long size = ((long)copy.BytesPerRow * (copy.Region.Height - 1)) + (copy.Region.Width * 4L);
                    copies.Add(new StagedCopy(reader.Kind, default, copy, Read(copy.Buffer, copy.BufferOffset, size)));
                }
            }

            Plans.Add([.. copies]);
            Result result = inner.Execute(plan);
            if (result.IsSuccess)
            {
                _running.Add((plan.FrameSlot, buffers));
            }

            return result;
        }

        public Result WaitForFrame(int frameSlot)
        {
            // Frames complete in submission order: the slot's last frame and every frame before it.
            Result result = inner.WaitForFrame(frameSlot);
            Complete(_running.FindLastIndex(frame => frame.Slot == frameSlot) + 1);
            return result;
        }

        public Result WaitIdle()
        {
            Result result = inner.WaitIdle();
            Complete(_running.Count);
            return result;
        }

        public FormatSupport GetFormatSupport(TextureFormat format) => inner.GetFormatSupport(format);

        public Result<ulong> CreateTexture(in TextureDescription description) => inner.CreateTexture(description);

        public Result<ulong> CreateTextureView(in TextureViewDescription description) => inner.CreateTextureView(description);

        public Result<ulong> CreateSampler(in SamplerDescription description) => inner.CreateSampler(description);

        public void SetDebugName(BackendObjectKind kind, ulong id, ReadOnlySpan<byte> utf8Name) => inner.SetDebugName(kind, id, utf8Name);

        public void Destroy(BackendObjectKind kind, ulong id)
        {
            inner.Destroy(kind, id);
            _ = _mapped.Remove(id);
        }

        public Result<ulong> CreateSurface(WindowSurfaceSource source) => inner.CreateSurface(source);

        public void DestroySurface(ulong surface) => inner.DestroySurface(surface);

        public Result<BackendSwapchain> CreateSwapchain(in BackendSwapchainRequest request) => inner.CreateSwapchain(request);

        public void DestroySwapchain(ulong swapchain) => inner.DestroySwapchain(swapchain);

        public void GetSwapchainImage(ulong swapchain, int imageIndex, out ulong texture, out ulong view) =>
            inner.GetSwapchainImage(swapchain, imageIndex, out texture, out view);

        public BackendAcquire AcquireImage(ulong swapchain, int frameSlot) => inner.AcquireImage(swapchain, frameSlot);

        public BackendPresent Present(ulong swapchain, int imageIndex) => inner.Present(swapchain, imageIndex);

        public Result<bool> IsFrameComplete(int frameSlot) => inner.IsFrameComplete(frameSlot);

        public Result BeginFrame(int frameSlot) => inner.BeginFrame(frameSlot);

        public bool TryReadTimestamps(int frameSlot, Span<ulong> ticks) => inner.TryReadTimestamps(frameSlot, ticks);

        public void InjectValidationError() => inner.InjectValidationError();

        public void Dispose() => inner.Dispose();

        // The staging memory must still be alive when the GPU reads it: reading a destroyed block throws.
        private byte[] Read(ulong buffer, long offset, long size) =>
            new ReadOnlySpan<byte>((byte*)_mapped[buffer] + offset, (int)size).ToArray();

        // Applies the oldest frames' copies, reading the staging memory now.
        private void Complete(int frames)
        {
            foreach ((int _, List<CopyBufferToBufferCommand> copies) in _running.Take(frames))
            {
                foreach (CopyBufferToBufferCommand copy in copies)
                {
                    Read(copy.Source, copy.SourceOffset, copy.Size).CopyTo(_contents[copy.Destination], copy.DestinationOffset);
                }
            }

            _running.RemoveRange(0, frames);
        }
    }
}
