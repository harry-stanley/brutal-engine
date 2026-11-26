using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Backend;
using BrutalEngine.Graphics.Recording;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Recording;

/// <summary>
/// The internal command writer (docs/design/graphics.md, "The frame plan"): the record bytes it writes, checked on the
/// raw stream without a reader; growth through the allocator; allocation-free steady state; and misuse.
/// </summary>
public sealed unsafe class CommandWriterTests
{
    public static TheoryData<int, int> PayloadSizesAndTrailingCounts()
    {
        var data = new TheoryData<int, int>();
        for (int size = 1; size <= 24; size++)
        {
            foreach (int count in (int[])[0, 1, 3])
            {
                data.Add(size, count);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PayloadSizesAndTrailingCounts))]
    public void Append_WritesHeaderPayloadAndPadding(int payloadSize, int trailingCount)
    {
        var allocator = new TrackingAllocator(new HeapAllocator(), "writer");
        using (var writer = new CommandWriter(allocator, initialCapacity: 8))
        {
            // Two records, so the second's start shows the first's padding, and a third with no trailing part.
            AppendSized(writer, payloadSize, CommandKind.PushConstants, seed: 0x10, trailingCount);
            AppendSized(writer, payloadSize, CommandKind.Barrier, seed: 0x40, trailingCount);
            writer.Append(CommandKind.Dispatch, new Sized4 { Value = 0x0A0B0C0D });

            // The expected stream, built byte by byte from the layout rule (header of kind, 0, size; the payload padded
            // with zeros to 8; the trailing part; zeros to 8), not from the writer's helpers. Trailing elements are
            // 3-byte values, so the trailing part's own length is rarely a multiple of 8.
            var expected = new List<byte>();
            AddRecord(expected, CommandKind.PushConstants, Pattern(payloadSize, 0x10), Pattern(trailingCount * 3, 0x90));
            AddRecord(expected, CommandKind.Barrier, Pattern(payloadSize, 0x40), Pattern(trailingCount * 3, 0xC0));
            AddRecord(expected, CommandKind.Dispatch, BitConverter.GetBytes(0x0A0B0C0D), []);

            Assert.Equal(expected.ToArray(), writer.Written.ToArray());
            fixed (byte* start = writer.Written)
            {
                Assert.Equal(0, (nint)start % 8);
            }
        }

        allocator.ThrowIfLeaks();
    }

    [Fact]
    public void Growth_UsesAllocatorAndFreesOnDispose()
    {
        var tracking = new TrackingAllocator(new HeapAllocator(), "writer");
        var poisoning = new PoisoningAllocator(tracking);
        var writer = new CommandWriter(poisoning, initialCapacity: 64);
        Assert.Equal(64, writer.Capacity);
        Assert.Equal(1, tracking.TotalAllocationCount);

        // Four 16-byte records fill the first block exactly; the fifth doubles it once.
        for (int i = 0; i < 4; i++)
        {
            writer.Append(CommandKind.EndRenderPass, new Sized4 { Value = i });
        }

        byte[] beforeGrowth = writer.Written.ToArray();
        Assert.Equal(64, beforeGrowth.Length);
        Assert.Equal(1, tracking.TotalAllocationCount);

        writer.Append(CommandKind.EndRenderPass, new Sized4 { Value = 4 });

        Assert.Equal(128, writer.Capacity);
        Assert.Equal(2, tracking.TotalAllocationCount);
        Assert.Equal(1, tracking.LiveAllocationCount);
        Assert.Equal(128, tracking.LiveBytes);
        // The old block was freed (and poisoned) only after the copy, so the earlier records are intact.
        Assert.Equal(beforeGrowth, writer.Written[..64].ToArray());
        Assert.Equal(80, writer.Written.Length);

        // A record larger than twice the capacity doubles as often as it needs: 128 -> 256 -> 512.
        writer.Append(CommandKind.Barrier, new Sized4 { Value = 5 }, new byte[300]);
        Assert.Equal(512, writer.Capacity);
        Assert.Equal(3, tracking.TotalAllocationCount);
        Assert.Equal(1, tracking.LiveAllocationCount);
        Assert.Equal(beforeGrowth, writer.Written[..64].ToArray());

        // Reset keeps the block.
        writer.Reset();
        Assert.Equal(0, writer.Written.Length);
        Assert.Equal(512, writer.Capacity);
        writer.Append(CommandKind.EndRenderPass, new Sized4 { Value = 6 });
        Assert.Equal(3, tracking.TotalAllocationCount);

        writer.Dispose();
        Assert.Equal(0, writer.Capacity);
        Assert.Equal(0, tracking.LiveAllocationCount);
        tracking.ThrowIfLeaks();

        writer.Dispose();
        Assert.Equal(3, tracking.TotalAllocationCount);
    }

    [Fact]
    public void Growth_AllocationFails_LeavesWriterUnchanged()
    {
        var tracking = new TrackingAllocator(new HeapAllocator(), "writer");
        var failing = new FailingAllocator(tracking);
        using (var writer = new CommandWriter(failing, initialCapacity: 64))
        {
            writer.Append(CommandKind.Draw, new Sized4 { Value = 1 });
            writer.Append(CommandKind.Draw, new Sized4 { Value = 2 });
            byte[] before = writer.Written.ToArray();

            failing.FailNext = true;
            Assert.Throws<InsufficientMemoryException>(() => writer.Append(CommandKind.Barrier, new Sized4 { Value = 3 }, new byte[64]));

            Assert.Equal(64, writer.Capacity);
            Assert.Equal(before, writer.Written.ToArray());
            Assert.Equal(1, tracking.LiveAllocationCount);

            // Still usable: a record that fits needs no growth, and growth works once the allocator does.
            writer.Append(CommandKind.Draw, new Sized4 { Value = 4 });
            writer.Append(CommandKind.Barrier, new Sized4 { Value = 5 }, new byte[64]);
            Assert.Equal(128, writer.Capacity);
            Assert.Equal(before, writer.Written[..32].ToArray());
            Assert.Equal(32 + 16 + 80, writer.Written.Length);
        }

        tracking.ThrowIfLeaks();

        // A failed first block leaves nothing allocated.
        var failingAtStart = new FailingAllocator(tracking) { FailNext = true };
        Assert.Throws<InsufficientMemoryException>(() => new CommandWriter(failingAtStart));
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Append_SteadyState_DoesNotAllocate()
    {
        var tracking = new TrackingAllocator(new HeapAllocator(), "writer");
        ReadOnlySpan<Sized3> trailing = [new Sized3(), new Sized3(), new Sized3()];
        using (var writer = new CommandWriter(tracking, initialCapacity: 8))
        {
            // Warm-up: grows the block to a frame's size and runs every generic instantiation once.
            WriteFrame(writer, trailing);
            int capacity = writer.Capacity;
            writer.Reset();

            long before = GC.GetAllocatedBytesForCurrentThread();
            int written = 0;
            for (int frame = 0; frame < 1_000; frame++)
            {
                WriteFrame(writer, trailing);
                written += writer.Written.Length;
                writer.Reset();
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(capacity, writer.Capacity);
            Assert.True(written > 0);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Misuse_Throws()
    {
        var tracking = new TrackingAllocator(new HeapAllocator(), "writer");
        Assert.Throws<ArgumentNullException>(() => new CommandWriter(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandWriter(tracking, initialCapacity: 7));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CommandWriter(tracking, initialCapacity: CommandWriter.MaxCapacity + 1));

        var writer = new CommandWriter(tracking, initialCapacity: 64);

        // Undefined kinds: above the last defined value, and the largest ushort.
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Append((CommandKind)22, new Sized4()));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Append((CommandKind)ushort.MaxValue, new Sized4()));

        // Trailing parts too large for the stream. The spans point at no memory: the writer must reject them before
        // reading a byte. One is over int.MaxValue bytes; the other fits an int but not the stream's maximum.
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Append(CommandKind.Barrier, new Sized4(), FakeSpan<long>(int.MaxValue / 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.Append(CommandKind.Barrier, new Sized4(), FakeSpan<byte>(CommandWriter.MaxCapacity - 8)));
        Assert.Equal(0, writer.Written.Length);
        Assert.Equal(1, tracking.TotalAllocationCount);

        writer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => writer.Append(CommandKind.Draw, new Sized4()));
        Assert.Throws<ObjectDisposedException>(() => writer.Append(CommandKind.Draw, new Sized4(), new byte[1]));
        Assert.Throws<ObjectDisposedException>(writer.Reset);
        Assert.Throws<ObjectDisposedException>(() => writer.Written.Length);
        tracking.ThrowIfLeaks();
    }

    private static void WriteFrame(CommandWriter writer, ReadOnlySpan<Sized3> trailing)
    {
        writer.Append(CommandKind.Barrier, new Sized4 { Value = 1 }, trailing);
        writer.Append(CommandKind.SetViewport, new Sized24());
        for (int i = 0; i < 64; i++)
        {
            writer.Append(CommandKind.Draw, new Sized16());
            writer.Append(CommandKind.PushConstants, new Sized12(), trailing);
        }
    }

    // Sound only because the writer rejects these spans before it reads them: the address is never dereferenced.
    private static ReadOnlySpan<T> FakeSpan<T>(int length)
        where T : unmanaged
        => new((void*)0x1000, length);

    // Appends a record whose payload is payloadSize bytes counting up from seed, with count 3-byte trailing elements
    // counting up from seed + 0x80.
    private static void AppendSized(CommandWriter writer, int payloadSize, CommandKind kind, byte seed, int count)
    {
        var trailing = new Sized3[count];
        Pattern(count * 3, (byte)(seed + 0x80)).CopyTo(MemoryMarshal.AsBytes(trailing.AsSpan()));
        switch (payloadSize)
        {
            case 1:
                Append<Sized1>();
                break;
            case 2:
                Append<Sized2>();
                break;
            case 3:
                Append<Sized3>();
                break;
            case 4:
                Append<Sized4>();
                break;
            case 5:
                Append<Sized5>();
                break;
            case 6:
                Append<Sized6>();
                break;
            case 7:
                Append<Sized7>();
                break;
            case 8:
                Append<Sized8>();
                break;
            case 9:
                Append<Sized9>();
                break;
            case 10:
                Append<Sized10>();
                break;
            case 11:
                Append<Sized11>();
                break;
            case 12:
                Append<Sized12>();
                break;
            case 13:
                Append<Sized13>();
                break;
            case 14:
                Append<Sized14>();
                break;
            case 15:
                Append<Sized15>();
                break;
            case 16:
                Append<Sized16>();
                break;
            case 17:
                Append<Sized17>();
                break;
            case 18:
                Append<Sized18>();
                break;
            case 19:
                Append<Sized19>();
                break;
            case 20:
                Append<Sized20>();
                break;
            case 21:
                Append<Sized21>();
                break;
            case 22:
                Append<Sized22>();
                break;
            case 23:
                Append<Sized23>();
                break;
            case 24:
                Append<Sized24>();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(payloadSize), payloadSize, "No payload of that size.");
        }

        void Append<T>()
            where T : unmanaged
        {
            Assert.Equal(payloadSize, Unsafe.SizeOf<T>());
            T payload = default;
            Pattern(payloadSize, seed).CopyTo(MemoryMarshal.AsBytes(new Span<T>(ref payload)));
            if (count == 0)
            {
                writer.Append(kind, payload);
            }
            else
            {
                writer.Append(kind, payload, (ReadOnlySpan<Sized3>)trailing);
            }
        }
    }

    private static void AddRecord(List<byte> stream, CommandKind kind, byte[] payload, byte[] trailing)
    {
        int payloadArea = (payload.Length + 7) / 8 * 8;
        int size = 8 + payloadArea + trailing.Length;
        stream.AddRange(BitConverter.GetBytes((ushort)kind));
        stream.AddRange([0, 0]);
        stream.AddRange(BitConverter.GetBytes(size));
        stream.AddRange(payload);
        stream.AddRange(new byte[payloadArea - payload.Length]);
        stream.AddRange(trailing);
        stream.AddRange(new byte[((size + 7) / 8 * 8) - size]);
    }

    private static byte[] Pattern(int length, byte seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)(seed + i + 1);
        }

        return bytes;
    }

    private struct Sized4
    {
        public int Value;
    }

    // Payloads of every size from 1 to 24 bytes, with no fields: StructLayout's Size gives the size, and the tests
    // fill the bytes through a span. AppendSized checks each size with Unsafe.SizeOf.
    [StructLayout(LayoutKind.Sequential, Size = 1)] private struct Sized1;
    [StructLayout(LayoutKind.Sequential, Size = 2)] private struct Sized2;
    [StructLayout(LayoutKind.Sequential, Size = 3)] private struct Sized3;
    [StructLayout(LayoutKind.Sequential, Size = 5)] private struct Sized5;
    [StructLayout(LayoutKind.Sequential, Size = 6)] private struct Sized6;
    [StructLayout(LayoutKind.Sequential, Size = 7)] private struct Sized7;
    [StructLayout(LayoutKind.Sequential, Size = 8)] private struct Sized8;
    [StructLayout(LayoutKind.Sequential, Size = 9)] private struct Sized9;
    [StructLayout(LayoutKind.Sequential, Size = 10)] private struct Sized10;
    [StructLayout(LayoutKind.Sequential, Size = 11)] private struct Sized11;
    [StructLayout(LayoutKind.Sequential, Size = 12)] private struct Sized12;
    [StructLayout(LayoutKind.Sequential, Size = 13)] private struct Sized13;
    [StructLayout(LayoutKind.Sequential, Size = 14)] private struct Sized14;
    [StructLayout(LayoutKind.Sequential, Size = 15)] private struct Sized15;
    [StructLayout(LayoutKind.Sequential, Size = 16)] private struct Sized16;
    [StructLayout(LayoutKind.Sequential, Size = 17)] private struct Sized17;
    [StructLayout(LayoutKind.Sequential, Size = 18)] private struct Sized18;
    [StructLayout(LayoutKind.Sequential, Size = 19)] private struct Sized19;
    [StructLayout(LayoutKind.Sequential, Size = 20)] private struct Sized20;
    [StructLayout(LayoutKind.Sequential, Size = 21)] private struct Sized21;
    [StructLayout(LayoutKind.Sequential, Size = 22)] private struct Sized22;
    [StructLayout(LayoutKind.Sequential, Size = 23)] private struct Sized23;
    [StructLayout(LayoutKind.Sequential, Size = 24)] private struct Sized24;

    // Fills every block with 0xDD when it is freed, so reading freed memory shows as wrong bytes.
    private sealed class PoisoningAllocator(INativeAllocator inner) : INativeAllocator
    {
        private readonly Dictionary<nint, nuint> _sizes = [];

        public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            void* memory = inner.Allocate(byteCount, alignment, callerFile, callerLine);
            _sizes.Add((nint)memory, byteCount);
            return memory;
        }

        public void Free(void* memory)
        {
            if (_sizes.Remove((nint)memory, out nuint size))
            {
                new Span<byte>(memory, (int)size).Fill(0xDD);
            }

            inner.Free(memory);
        }
    }

    // Throws InsufficientMemoryException from the next allocation when FailNext is set, once.
    private sealed class FailingAllocator(INativeAllocator inner) : INativeAllocator
    {
        public bool FailNext { get; set; }

        public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InsufficientMemoryException("Injected allocation failure.");
            }

            return inner.Allocate(byteCount, alignment, callerFile, callerLine);
        }

        public void Free(void* memory) => inner.Free(memory);
    }
}
