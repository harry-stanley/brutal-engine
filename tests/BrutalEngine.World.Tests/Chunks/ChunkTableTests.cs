using System;
using System.Runtime.CompilerServices;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using Xunit;

namespace BrutalEngine.World.Tests.Chunks;

public sealed unsafe class ChunkTableTests
{
    private const int ChunkBytes = 16 * 1024;

    [Fact]
    public void Metadata_GrowsWithChunks()
    {
        TrackingAllocator tracking = NewTracking();
        TestAllocator allocator = new(tracking);
        ChunkPool pool = new(allocator, ChunkBytes);
        ChunkTable table = new(allocator, pool, columnCount: 3);

        // Nothing is allocated until the first chunk.
        Assert.Equal(0, table.Capacity);
        Assert.Equal(0, tracking.LiveAllocationCount);

        int[] expectedCapacities = [4, 4, 4, 4, 8, 8, 8, 8, 16, 16];
        for (int i = 0; i < expectedCapacities.Length; i++)
        {
            uint tick = Tick(i);
            Assert.Equal(i, table.AddChunk(tick));
            Assert.Equal(i + 1, table.ChunkCount);
            Assert.Equal(expectedCapacities[i], table.Capacity);

            // The allocator poisons every block, so ticks the table didn't write can't read as the creating tick.
            Assert.Equal(0, table.GetCount(i));
            Assert.Equal([tick, tick, tick], table.GetChanged(i).ToArray());
            Assert.Equal([tick, tick, tick], table.GetAdded(i).ToArray());

            // Distinct stamps and counts, so a growth that re-initialised or dropped them would show.
            table.GetChanged(i)[1] = tick + 1;
            table.GetAdded(i)[2] = tick + 2;
            table.SetCount(i, i + 1);
        }

        // Every chunk's entry and ticks survived both growths, and each chunk has its own aligned block.
        for (int i = 0; i < expectedCapacities.Length; i++)
        {
            uint tick = Tick(i);
            Assert.Equal(i + 1, table.GetCount(i));
            Assert.Equal([tick, tick + 1, tick], table.GetChanged(i).ToArray());
            Assert.Equal([tick, tick, tick + 2], table.GetAdded(i).ToArray());
            Assert.Equal(0, (nint)table.GetData(i) % 64);
            for (int j = 0; j < i; j++)
            {
                Assert.True(table.GetData(i) != table.GetData(j));
            }
        }

        // Ten blocks plus the three arrays: growth freed the old arrays.
        Assert.Equal(13, tracking.LiveAllocationCount);
        table.Dispose();
        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AddChunk_AfterRemove_TicksStartAtNewTick()
    {
        TrackingAllocator tracking = NewTracking();
        ChunkPool pool = new(tracking, ChunkBytes);
        ChunkTable table = new(tracking, pool, columnCount: 2);
        _ = table.AddChunk(5);
        table.GetChanged(0)[0] = 6;
        table.GetAdded(0)[1] = 7;
        table.RemoveLastChunk();

        // The slot's old ticks are overwritten, not inherited.
        Assert.Equal(0, table.AddChunk(9));
        Assert.Equal([9u, 9u], table.GetChanged(0).ToArray());
        Assert.Equal([9u, 9u], table.GetAdded(0).ToArray());
        Assert.Equal(0, table.GetCount(0));

        table.Dispose();
        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AddChunk_NoColumns_HasNoTickArrays()
    {
        TrackingAllocator tracking = NewTracking();
        ChunkPool pool = new(tracking, ChunkBytes);
        ChunkTable table = new(tracking, pool, columnCount: 0);
        for (int i = 0; i < 5; i++)
        {
            _ = table.AddChunk(Tick(i));
            Assert.True(table.GetChanged(i).IsEmpty);
            Assert.True(table.GetAdded(i).IsEmpty);
        }

        // Five blocks and the entry array only: a zero-byte tick array isn't allocated.
        Assert.Equal(6, tracking.LiveAllocationCount);
        table.Dispose();
        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// Fails each allocation of a growing <see cref="ChunkTable.AddChunk"/> in turn, from an empty pool: 0 is the
    /// block, 1 the entry array, 2 the Changed ticks and 3 the Added ticks (an archetype without columns has none).
    /// </summary>
    [Theory]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 2)]
    [InlineData(3, 3)]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    public void AddChunk_AllocationFails_LeavesTableUnchanged(int columnCount, int failingAllocation)
    {
        TrackingAllocator tracking = NewTracking();
        TestAllocator allocator = new(tracking);
        ChunkPool pool = new(allocator, ChunkBytes);
        ChunkTable table = new(allocator, pool, columnCount);
        nint[] data = new nint[ChunkTable.InitialCapacity];
        uint[][] changed = new uint[data.Length][];
        uint[][] added = new uint[data.Length][];
        for (int i = 0; i < data.Length; i++)
        {
            _ = table.AddChunk(Tick(i));
            table.SetCount(i, i + 1);
            if (columnCount > 0)
            {
                table.GetChanged(i)[0] = Tick(i) + 1;
                table.GetAdded(i)[^1] = Tick(i) + 2;
            }

            data[i] = (nint)table.GetData(i);
            changed[i] = table.GetChanged(i).ToArray();
            added[i] = table.GetAdded(i).ToArray();
        }

        allocator.FailAfter = failingAllocation;
        _ = Assert.Throws<InsufficientMemoryException>(() => table.AddChunk(99));

        // The injected failure fired, so this row tested the allocation it names.
        Assert.Equal(-1, allocator.FailAfter);
        Assert.Equal(data.Length, table.ChunkCount);
        Assert.Equal(ChunkTable.InitialCapacity, table.Capacity);
        for (int i = 0; i < data.Length; i++)
        {
            Assert.Equal(data[i], (nint)table.GetData(i));
            Assert.Equal(i + 1, table.GetCount(i));
            Assert.Equal(changed[i], table.GetChanged(i).ToArray());
            Assert.Equal(added[i], table.GetAdded(i).ToArray());
        }

        // A block rented before the failure went back to the pool, and nothing the failed growth allocated is live.
        Assert.Equal(data.Length, pool.RentedCount);
        Assert.Equal(failingAllocation == 0 ? 0 : 1, pool.PooledCount);
        int arrays = columnCount == 0 ? 1 : 3;
        Assert.Equal(data.Length + arrays + pool.PooledCount, tracking.LiveAllocationCount);

        // Still usable: the next call grows and keeps every chunk.
        Assert.Equal(data.Length, table.AddChunk(99));
        Assert.Equal(2 * ChunkTable.InitialCapacity, table.Capacity);
        Assert.Equal(changed[0], table.GetChanged(0).ToArray());
        Assert.All(table.GetChanged(data.Length).ToArray(), tick => Assert.Equal(99u, tick));

        table.Dispose();
        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// Growth has swapped in the new arrays and freed the old entry array when the free of the old Changed array
    /// throws: the table must keep every chunk on the grown arrays, not keep a freed entry array and leak the new ones.
    /// </summary>
    [Fact]
    public void AddChunk_FreeOfOldArrayThrows_TableUsesGrownArrays()
    {
        TrackingAllocator tracking = NewTracking();
        TestAllocator allocator = new(tracking);
        ChunkPool pool = new(allocator, ChunkBytes);
        ChunkTable table = new(allocator, pool, columnCount: 3);
        nint[] data = new nint[ChunkTable.InitialCapacity];
        for (int i = 0; i < data.Length; i++)
        {
            _ = table.AddChunk(Tick(i));
            table.SetCount(i, i + 1);
            table.GetChanged(i)[0] = Tick(i) + 1;
            data[i] = (nint)table.GetData(i);
        }

        allocator.FailFreeAfter = 1;
        _ = Assert.Throws<InvalidOperationException>(() => table.AddChunk(99));

        // The injected failure fired, and the table already uses the grown arrays with every chunk intact.
        Assert.Equal(-1, allocator.FailFreeAfter);
        Assert.Equal(2 * ChunkTable.InitialCapacity, table.Capacity);
        Assert.Equal(data.Length, table.ChunkCount);
        for (int i = 0; i < data.Length; i++)
        {
            Assert.Equal(data[i], (nint)table.GetData(i));
            Assert.Equal(i + 1, table.GetCount(i));
            Assert.Equal([Tick(i) + 1, Tick(i), Tick(i)], table.GetChanged(i).ToArray());
            Assert.Equal([Tick(i), Tick(i), Tick(i)], table.GetAdded(i).ToArray());
        }

        // No chunk was added: the block went back to the pool, and the next add reuses it without growing.
        Assert.Equal(data.Length, pool.RentedCount);
        Assert.Equal(1, pool.PooledCount);
        int allocateCalls = allocator.AllocateCalls;
        Assert.Equal(data.Length, table.AddChunk(99));
        Assert.Equal(allocateCalls, allocator.AllocateCalls);

        // Disposal frees the grown arrays once each; only the two old tick arrays whose frees failed or never ran leak.
        table.Dispose();
        pool.Dispose();
        NativeAllocationInfo[] leaked = tracking.GetLiveAllocations();
        Assert.Equal(2, leaked.Length);
        foreach (NativeAllocationInfo allocation in leaked)
        {
            Assert.Equal((nuint)(ChunkTable.InitialCapacity * 3 * sizeof(uint)), allocation.ByteCount);
            tracking.Free((void*)allocation.Address);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void RemoveLastChunk_ReturnsBlockToPool()
    {
        TrackingAllocator tracking = NewTracking();
        TestAllocator allocator = new(tracking);
        ChunkPool pool = new(allocator, ChunkBytes);
        ChunkTable table = new(allocator, pool, columnCount: 2);
        _ = table.AddChunk(1);
        _ = table.AddChunk(2);
        byte* last = table.GetData(1);
        int allocateCalls = allocator.AllocateCalls;

        table.RemoveLastChunk();
        Assert.Equal(1, table.ChunkCount);
        Assert.Equal(1, pool.RentedCount);
        Assert.Equal(1, pool.PooledCount);

        // The next chunk reuses the block without asking the allocator, and nothing was freed.
        Assert.Equal(1, table.AddChunk(3));
        Assert.True(table.GetData(1) == last);
        Assert.Equal(allocateCalls, allocator.AllocateCalls);
        Assert.Equal(0, allocator.FreeCalls);
        Assert.Equal(0, pool.PooledCount);

        table.Dispose();
        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void RemoveLastChunk_NoChunks_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        ChunkPool pool = new(tracking, ChunkBytes);
        ChunkTable table = new(tracking, pool, columnCount: 1);

        _ = Assert.Throws<InvalidOperationException>(table.RemoveLastChunk);
        _ = table.AddChunk(1);
        table.RemoveLastChunk();
        _ = Assert.Throws<InvalidOperationException>(table.RemoveLastChunk);
        Assert.Equal(1, pool.PooledCount);

        table.Dispose();
        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void RemoveLastChunk_NotEmpty_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        ChunkPool pool = new(tracking, ChunkBytes);
        ChunkTable table = new(tracking, pool, columnCount: 1);
        _ = table.AddChunk(1);
        table.SetCount(0, 1);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(table.RemoveLastChunk);
        Assert.Contains("1 rows", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, table.ChunkCount);
        Assert.Equal(1, pool.RentedCount);

        table.Dispose();
        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_NoLeaks()
    {
        TrackingAllocator tracking = NewTracking();
        ChunkPool pool = new(tracking, ChunkBytes);
        ChunkTable table = new(tracking, pool, columnCount: 4);
        for (int i = 0; i < 6; i++)
        {
            _ = table.AddChunk(Tick(i));
            table.SetCount(i, 3);
        }

        table.SetCount(5, 0);
        table.RemoveLastChunk();

        // Every block goes back to the pool, rows or not, and the arrays are freed: only the six pooled blocks remain.
        table.Dispose();
        Assert.Equal(0, pool.RentedCount);
        Assert.Equal(6, pool.PooledCount);
        Assert.Equal(6, tracking.LiveAllocationCount);
        Assert.Equal(0, table.ChunkCount);
        Assert.Equal(0, table.Capacity);

        // A second Dispose returns and frees nothing twice (the pool and the tracking allocator would throw).
        table.Dispose();
        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_PoolDisposedFirst_FreesArraysAndThrows()
    {
        TrackingAllocator tracking = NewTracking();
        ChunkPool pool = new(tracking, ChunkBytes);
        ChunkTable table = new(tracking, pool, columnCount: 2);
        _ = table.AddChunk(1);
        byte* block = table.GetData(0);
        pool.Dispose();

        // Misuse: the blocks can't go back, but the arrays are still freed and the table is disposed.
        _ = Assert.Throws<ObjectDisposedException>(table.Dispose);
        Assert.Equal(1, tracking.LiveAllocationCount);
        table.Dispose();
        _ = Assert.Throws<ObjectDisposedException>(() => table.AddChunk(2));

        tracking.Free(block);
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_FreeThrows_TableDisposedAndNothingFreedTwice()
    {
        TrackingAllocator tracking = NewTracking();
        TestAllocator allocator = new(tracking);
        ChunkPool pool = new(allocator, ChunkBytes);
        ChunkTable table = new(allocator, pool, columnCount: 2);
        _ = table.AddChunk(1);
        _ = table.AddChunk(2);

        // The entry array is freed, then the free of the Changed array throws.
        allocator.FailFreeAfter = 1;
        _ = Assert.Throws<InvalidOperationException>(table.Dispose);
        Assert.Equal(-1, allocator.FailFreeAfter);

        // The blocks went back before the frees, and the table is disposed.
        Assert.Equal(0, pool.RentedCount);
        Assert.Equal(2, pool.PooledCount);
        Assert.Equal(0, table.ChunkCount);
        Assert.Equal(0, table.Capacity);
        _ = Assert.Throws<ObjectDisposedException>(() => table.AddChunk(3));

        // A later Dispose frees nothing again; the two tick arrays not freed leak, as documented.
        int freeCalls = allocator.FreeCalls;
        table.Dispose();
        Assert.Equal(freeCalls, allocator.FreeCalls);
        pool.Dispose();
        NativeAllocationInfo[] leaked = tracking.GetLiveAllocations();
        Assert.Equal(2, leaked.Length);
        foreach (NativeAllocationInfo allocation in leaked)
        {
            Assert.Equal((nuint)(ChunkTable.InitialCapacity * 2 * sizeof(uint)), allocation.ByteCount);
            tracking.Free((void*)allocation.Address);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AfterDispose_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        ChunkPool pool = new(tracking, ChunkBytes);
        ChunkTable table = new(tracking, pool, columnCount: 1);
        _ = table.AddChunk(1);
        table.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => table.AddChunk(2));
        _ = Assert.Throws<ObjectDisposedException>(table.RemoveLastChunk);
        _ = Assert.Throws<ObjectDisposedException>(() => (nint)table.GetData(0));
        _ = Assert.Throws<ObjectDisposedException>(() => table.GetCount(0));
        _ = Assert.Throws<ObjectDisposedException>(() => table.SetCount(0, 0));
        _ = Assert.Throws<ObjectDisposedException>(() => table.GetChanged(0).Length);
        _ = Assert.Throws<ObjectDisposedException>(() => table.GetAdded(0).Length);
        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(int.MinValue)]
    public void Accessors_ChunkOutOfRange_Throw(int chunk)
    {
        TrackingAllocator tracking = NewTracking();
        ChunkPool pool = new(tracking, ChunkBytes);
        ChunkTable table = new(tracking, pool, columnCount: 1);

        // One chunk in a four-chunk array: index 1 is within the capacity but isn't a chunk.
        _ = table.AddChunk(1);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => (nint)table.GetData(chunk));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => table.GetCount(chunk));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => table.SetCount(chunk, 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => table.GetChanged(chunk).Length);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => table.GetAdded(chunk).Length);

        table.Dispose();
        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void SetCount_Negative_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        ChunkPool pool = new(tracking, ChunkBytes);
        ChunkTable table = new(tracking, pool, columnCount: 1);
        _ = table.AddChunk(1);
        table.SetCount(0, 2);

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => table.SetCount(0, -1));
        Assert.Equal("count", exception.ParamName);
        Assert.Equal(2, table.GetCount(0));

        table.SetCount(0, 0);
        table.Dispose();
        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Constructor_InvalidArguments_Throw()
    {
        TrackingAllocator tracking = NewTracking();
        using ChunkPool pool = new(tracking, ChunkBytes);

        Assert.Equal("allocator", Assert.Throws<ArgumentNullException>(() => new ChunkTable(null!, pool, 1)).ParamName);
        Assert.Equal("pool", Assert.Throws<ArgumentNullException>(() => new ChunkTable(tracking, null!, 1)).ParamName);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkTable(tracking, pool, -1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkTable(tracking, pool, 4097));

        // The widest archetype a world allows is accepted.
        using ChunkTable widest = new(tracking, pool, 4096);
        Assert.Equal(4096, widest.ColumnCount);
    }

    [Fact]
    public void AddRemove_SteadyState_AllocatesNoManagedMemory()
    {
        TrackingAllocator tracking = NewTracking();
        ChunkPool pool = new(tracking, ChunkBytes);
        ChunkTable table = new(tracking, pool, columnCount: 3);

        // Warm up: the arrays and a pooled block exist, and every member has run once.
        Churn(table);
        long nativeAllocations = tracking.TotalAllocationCount;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            Churn(table);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(nativeAllocations, tracking.TotalAllocationCount);

        table.Dispose();
        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    private static void Churn(ChunkTable table)
    {
        int chunk = table.AddChunk(7);
        table.SetCount(chunk, 1);
        table.GetChanged(chunk)[0] = table.GetAdded(chunk)[1] + (uint)table.GetCount(chunk);
        table.SetCount(chunk, 0);
        table.RemoveLastChunk();
    }

    private static uint Tick(int chunk) => 100u + ((uint)chunk * 10);

    /// <summary>A fresh tracking allocator per test, so each test's leak check sees only its own blocks.</summary>
    private static TrackingAllocator NewTracking() => new(new HeapAllocator(), "ChunkTable");

    /// <summary>
    /// Passes calls through, counts them, fills every new allocation with 0xA5 so uninitialised memory is
    /// recognisable, throws <see cref="InsufficientMemoryException"/> (an out-of-memory exception) once a set
    /// number of allocations succeed, and throws <see cref="InvalidOperationException"/> without freeing once a set
    /// number of frees succeed.
    /// </summary>
    private sealed class TestAllocator(INativeAllocator inner) : INativeAllocator
    {
        /// <summary>Allocations that still succeed before one fails; -1 never fails.</summary>
        public int FailAfter { get; set; } = -1;

        public int AllocateCalls { get; private set; }

        /// <summary>Frees that still succeed before one throws without freeing; -1 never throws.</summary>
        public int FailFreeAfter { get; set; } = -1;

        public int FreeCalls { get; private set; }

        public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            AllocateCalls++;
            if (FailAfter == 0)
            {
                FailAfter = -1;
                throw new InsufficientMemoryException("Injected allocation failure.");
            }

            if (FailAfter > 0)
            {
                FailAfter--;
            }

            void* memory = inner.Allocate(byteCount, alignment, callerFile, callerLine);
            new Span<byte>(memory, checked((int)byteCount)).Fill(0xA5);
            return memory;
        }

        public void Free(void* memory)
        {
            FreeCalls++;
            if (FailFreeAfter == 0)
            {
                FailFreeAfter = -1;
                throw new InvalidOperationException("Injected free failure.");
            }

            if (FailFreeAfter > 0)
            {
                FailFreeAfter--;
            }

            inner.Free(memory);
        }
    }
}
