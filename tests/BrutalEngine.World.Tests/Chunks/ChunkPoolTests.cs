using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using Xunit;

namespace BrutalEngine.World.Tests.Chunks;

public sealed unsafe class ChunkPoolTests
{
    private const int ChunkBytes = 16 * 1024;

    [Fact]
    public void Rent_Aligned64()
    {
        TrackingAllocator tracking = NewTracking();
        nint[] blocks = new nint[8];
        using (ChunkPool pool = new(tracking, ChunkBytes))
        {
            for (int i = 0; i < blocks.Length; i++)
            {
                blocks[i] = (nint)pool.Rent();
                Assert.Equal(0, blocks[i] % 64);
            }

            // Every block was asked of the allocator at the chunk size and 64-byte alignment, so the alignment above
            // isn't luck.
            foreach (NativeAllocationInfo allocation in tracking.GetLiveAllocations())
            {
                Assert.Equal((nuint)ChunkBytes, allocation.ByteCount);
                Assert.Equal((nuint)64, allocation.Alignment);
            }

            Assert.Equal(blocks.Length, tracking.LiveAllocationCount);
            Assert.Equal(blocks.Length, pool.RentedCount);
            ReturnAll(pool, blocks);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void ReturnRent_NoAllocatorCall()
    {
        TrackingAllocator tracking = NewTracking();
        CountingAllocator counting = new(tracking);
        using (ChunkPool pool = new(counting, ChunkBytes))
        {
            nint[] first = [(nint)pool.Rent(), (nint)pool.Rent(), (nint)pool.Rent()];
            ReturnAll(pool, first);
            Assert.Equal(3, counting.AllocateCalls);
            Assert.Equal(3, pool.PooledCount);
            Assert.Equal(0, pool.RentedCount);

            // The same blocks come back, most recently returned first, and the allocator isn't called.
            nint[] second = [(nint)pool.Rent(), (nint)pool.Rent(), (nint)pool.Rent()];
            Assert.Equal([first[2], first[1], first[0]], second);
            Assert.Equal(3, counting.AllocateCalls);
            Assert.Equal(0, counting.FreeCalls);
            Assert.Equal(0, pool.PooledCount);
            Assert.Equal(3, pool.RentedCount);

            // With the pool empty, the next rent does call the allocator.
            nint fourth = (nint)pool.Rent();
            Assert.Equal(4, counting.AllocateCalls);
            ReturnAll(pool, [.. second, fourth]);
            Assert.Equal(0, counting.FreeCalls);
        }

        Assert.Equal(4, counting.FreeCalls);
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Trim_AndDispose_NoLeaks()
    {
        TrackingAllocator tracking = NewTracking();
        ChunkPool pool = new(tracking, ChunkBytes);
        byte* kept = pool.Rent();
        byte* returned = pool.Rent();
        pool.Return(returned);

        pool.Trim();

        // Only the pooled block is freed; the rented one is untouched and still usable.
        Assert.Equal(0, pool.PooledCount);
        Assert.Equal(1, pool.RentedCount);
        Assert.Equal(1, tracking.LiveAllocationCount);
        new Span<byte>(kept, ChunkBytes).Fill(0xAB);

        // A rent after a trim allocates again.
        byte* fresh = pool.Rent();
        Assert.Equal(2, tracking.LiveAllocationCount);
        pool.Return(fresh);
        pool.Return(kept);

        pool.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Rent_AllocatorFails_PoolUnchanged()
    {
        TrackingAllocator tracking = NewTracking();
        CountingAllocator counting = new(tracking) { Fail = true };
        using (ChunkPool pool = new(counting, ChunkBytes))
        {
            _ = Assert.Throws<InsufficientMemoryException>(() => pool.Rent());
            Assert.Equal(0, pool.RentedCount);
            Assert.Equal(0, pool.PooledCount);

            counting.Fail = false;
            byte* block = pool.Rent();
            Assert.Equal(1, pool.RentedCount);
            pool.Return(block);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Trim_FreeThrowsPartway_PooledCountMatchesList()
    {
        TrackingAllocator tracking = NewTracking();
        CountingAllocator counting = new(tracking) { ThrowOnFreeCall = 2 };
        ChunkPool pool = new(counting, ChunkBytes);
        nint[] blocks = [(nint)pool.Rent(), (nint)pool.Rent(), (nint)pool.Rent()];
        ReturnAll(pool, blocks);

        // The second free throws without freeing: one block is freed, and the two left are still pooled and counted.
        _ = Assert.Throws<InvalidOperationException>(pool.Trim);
        Assert.Equal(2, pool.PooledCount);
        Assert.Equal(2, tracking.LiveAllocationCount);

        // A later Dispose frees the rest, the block whose free failed included.
        pool.Dispose();
        Assert.Equal(0, pool.PooledCount);
        Assert.Equal(4, counting.FreeCalls);
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_FreeThrowsPartway_LaterDisposeFreesRest()
    {
        TrackingAllocator tracking = NewTracking();
        CountingAllocator counting = new(tracking) { ThrowOnFreeCall = 2 };
        ChunkPool pool = new(counting, ChunkBytes);
        nint[] blocks = [(nint)pool.Rent(), (nint)pool.Rent(), (nint)pool.Rent()];
        ReturnAll(pool, blocks);

        // The second free throws without freeing: the pool stays undisposed, with the two blocks left still pooled.
        _ = Assert.Throws<InvalidOperationException>(pool.Dispose);
        Assert.Equal(2, pool.PooledCount);
        Assert.Equal(2, tracking.LiveAllocationCount);

        // A later Dispose frees the rest, the block whose free failed included.
        pool.Dispose();
        Assert.Equal(0, pool.PooledCount);
        Assert.Equal(4, counting.FreeCalls);
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Return_Null_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        using (ChunkPool pool = new(tracking, ChunkBytes))
        {
            byte* block = pool.Rent();
            ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => pool.Return(null));
            Assert.Equal("block", exception.ParamName);
            Assert.Equal(1, pool.RentedCount);
            pool.Return(block);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Return_NothingRented_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        using (ChunkPool pool = new(tracking, ChunkBytes))
        {
            byte* block = pool.Rent();
            pool.Return(block);

            // A second return of the same block, with nothing rented, is caught before it corrupts the free list.
            _ = Assert.Throws<InvalidOperationException>(() => pool.Return(block));
            Assert.Equal(1, pool.PooledCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AfterDispose_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        ChunkPool pool = new(tracking, ChunkBytes);
        byte* block = pool.Rent();
        pool.Return(block);
        pool.Dispose();
        pool.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => pool.Rent());
        _ = Assert.Throws<ObjectDisposedException>(() => pool.Return(block));
        _ = Assert.Throws<ObjectDisposedException>(pool.Trim);
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Constructor_InvalidArguments_Throws()
    {
        _ = Assert.Throws<ArgumentNullException>(() => new ChunkPool(null!, ChunkBytes));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkPool(new HeapAllocator(), 7));

        // Eight bytes is the smallest block: room for the free-list link.
        TrackingAllocator tracking = NewTracking();
        using (ChunkPool pool = new(tracking, 8))
        {
            byte* block = pool.Rent();
            pool.Return(block);
            Assert.True(pool.Rent() == block);
            pool.Return(block);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void RentReturn_SteadyState_AllocatesNoManagedMemory()
    {
        TrackingAllocator tracking = NewTracking();
        using (ChunkPool pool = new(tracking, ChunkBytes))
        {
            // Warm up: the blocks exist and every member has run once.
            ChurnOnce(pool);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                ChurnOnce(pool);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(2, tracking.TotalAllocationCount);
        }

        tracking.ThrowIfLeaks();
    }

    private static void ChurnOnce(ChunkPool pool)
    {
        byte* a = pool.Rent();
        byte* b = pool.Rent();
        pool.Return(a);
        pool.Return(b);
    }

    private static void ReturnAll(ChunkPool pool, IEnumerable<nint> blocks)
    {
        foreach (nint block in blocks)
        {
            pool.Return((byte*)block);
        }
    }

    /// <summary>A fresh tracking allocator per test, so each test's leak check sees only its own blocks.</summary>
    private static TrackingAllocator NewTracking() => new(new HeapAllocator(), "ChunkPool");

    /// <summary>
    /// Passes calls through and counts them; throws an out-of-memory exception from Allocate while <see cref="Fail"/>
    /// is set, and throws from the Free call numbered <see cref="ThrowOnFreeCall"/> without freeing.
    /// </summary>
    private sealed class CountingAllocator(INativeAllocator inner) : INativeAllocator
    {
        public int AllocateCalls { get; private set; }

        public int FreeCalls { get; private set; }

        public bool Fail { get; set; }

        /// <summary>The 1-based Free call that throws instead of freeing; 0 never throws.</summary>
        public int ThrowOnFreeCall { get; init; }

        public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            AllocateCalls++;
            return Fail
                ? throw new InsufficientMemoryException("Injected allocation failure.")
                : inner.Allocate(byteCount, alignment, callerFile, callerLine);
        }

        public void Free(void* memory)
        {
            FreeCalls++;
            if (FreeCalls == ThrowOnFreeCall)
            {
                throw new InvalidOperationException("Injected free failure.");
            }

            inner.Free(memory);
        }
    }
}
