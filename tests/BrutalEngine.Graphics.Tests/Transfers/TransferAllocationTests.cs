using System;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Tests.Device;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Transfers;

/// <summary>
/// Steady-state frames with transient allocations, copies and readback allocate no managed memory (docs/design/graphics.md
/// section 14).
/// </summary>
public sealed class TransferAllocationTests : IDisposable
{
    private const int Frames = 1000;
    private const int WarmUpFrames = 8;

    private static readonly TransientUsage[] _usages =
        [TransientUsage.Vertex, TransientUsage.Index, TransientUsage.Uniform, TransientUsage.Storage, TransientUsage.Indirect];

    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void SteadyTransientFrames_DoNotAllocate()
    {
        _fixture.Script.RecordCalls = false;
        GraphicsDevice device = _fixture.Create();

        // Every frame allocates once per usage from its slot's ring, writes the memory and reads every field, then
        // submits, which makes the frame's alias stale; the warm-up frames make both slots' rings and an alias table slot.
        long checksum = 0;
        long before = 0;
        for (int i = 0; i < Frames + WarmUpFrames; i++)
        {
            if (i == WarmUpFrames)
            {
                before = GC.GetAllocatedBytesForCurrentThread();
            }

            FrameStatus begun = device.BeginFrame(out CommandList commands);
            foreach (TransientUsage usage in _usages)
            {
                bool allocated = commands.AllocateTransient(64, usage, out TransientAllocation allocation).IsSuccess;
                allocation.Data.Fill((byte)i);
                checksum += allocation.Offset + allocation.Data.Length + (allocation.Buffer.IsNull ? -100_000 : 0) + (allocated ? 0 : -100_000);
            }

            FrameStatus submitted = device.SubmitFrame(commands);
            GraphicsStatistics statistics = device.Statistics;
            checksum += (long)begun + (long)submitted + statistics.LiveObjects + statistics.UploadOverflowBytes;
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocatedBytes);
        // The fake's uniform and storage alignments are 256: offsets 0, 64, 256, 512 and 576, 64 bytes each.
        Assert.Equal((Frames + WarmUpFrames) * (0 + 64 + 256 + 512 + 576 + (5 * 64L)), checksum);
        Assert.True(device.WaitIdle().IsSuccess);
        device.ThrowIfLeaks();
    }

    [Fact]
    public void SteadyCopyFrames_DoNotAllocate()
    {
        _fixture.Script.RecordCalls = false;
        GraphicsDevice device = _fixture.Create();
        var resources = TransferResources.Create(device);
        BufferHandle settled = device.CreateBuffer(new BufferDescription { Size = 64, Usage = BufferUsage.CopyDestination, Memory = MemoryIntent.Readback }).Value;
        Span<byte> read = stackalloc byte[16];

        // Every frame records each copy kind (one from a transient allocation into the readback buffer), writes the
        // readback buffer, and reads it (pending: false) and a buffer written only in the first frame (true after the
        // warm-up). The warm-up frames grow both slots' streams and rings and make an alias table slot.
        long checksum = 0;
        long before = 0;
        for (int i = 0; i < Frames + WarmUpFrames; i++)
        {
            if (i == WarmUpFrames)
            {
                before = GC.GetAllocatedBytesForCurrentThread();
            }

            _ = device.BeginFrame(out CommandList commands);
            foreach (CommandKind kind in TransferResources.Kinds)
            {
                resources.Record(commands, kind);
            }

            bool allocated = commands.AllocateTransient(16, TransientUsage.Vertex, out TransientAllocation source).IsSuccess;
            commands.CopyBufferToBuffer(new BufferCopy { Source = source.Buffer, SourceOffset = source.Offset, Destination = i == 0 ? settled : resources.Readback, Size = 16 });
            bool wrote = device.WriteBuffer(resources.Readback, 16, read).IsSuccess;
            bool pending = device.TryReadBuffer(resources.Readback, 0, read);
            bool settledRead = device.TryReadBuffer(settled, 48, read);
            _ = device.SubmitFrame(commands);
            checksum += (allocated && wrote ? 1 : -100_000) + (pending ? -100_000 : 0) + (settledRead ? 1 : 0);
        }

        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocatedBytes);
        // Every frame allocated and wrote; the settled buffer is readable from frame 2 on (two frames in flight).
        Assert.Equal((Frames + WarmUpFrames) + (Frames + WarmUpFrames - 2), checksum);
        Assert.True(device.WaitIdle().IsSuccess);
    }
}
