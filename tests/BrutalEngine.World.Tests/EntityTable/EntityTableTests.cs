using System;
using System.Runtime.CompilerServices;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using Xunit;

namespace BrutalEngine.World.Tests.EntityTable;

// The folder (and so this namespace) is named EntityTable, which hides the type of the same name; the tests name it
// as Storage.EntityTable.
public sealed class EntityTableTests
{
    [Fact]
    public void Reuse_IsFifoWithNewGeneration()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "EntityTable");
        using (Storage.EntityTable table = new(tracking, initialCapacity: 8, maxEntities: 100))
        {
            for (int i = 0; i < 4; i++)
            {
                Entity entity = table.Allocate();
                Assert.Equal(new Entity((uint)i, 1), entity);
                table.Place(i, archetype: 0, chunk: 0, row: i);
            }

            table.Free(2);
            table.Free(0);
            table.Free(3);

            // Freed slots come back in the order they were freed, each one generation on.
            Assert.Equal(new Entity(2, 2), table.Allocate());
            Assert.Equal(new Entity(0, 2), table.Allocate());
            Assert.Equal(new Entity(3, 2), table.Allocate());

            // With the free queue empty, a fresh slot is issued at generation 1.
            Assert.Equal(new Entity(4, 1), table.Allocate());
            Assert.Equal(5, table.SlotCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void MaxGeneration_Retires()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 100))
        {
            _ = table.Allocate();
            table.Place(0, archetype: 0, chunk: 0, row: 0);
            table.SetGenerationForTesting(0, uint.MaxValue);

            table.Free(0);

            EntityRecord record = table.GetRecord(0);
            Assert.Equal(EntityRecord.RetiredState, record.Archetype);
            Assert.Equal(uint.MaxValue, record.Generation);
            Assert.Equal(1, table.RetiredCount);
            Assert.Equal(0, table.FreeCount);

            // The retired slot is never reused: the next entity gets a fresh slot.
            Assert.Equal(new Entity(1, 1), table.Allocate());
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void MaxGeneration_RetiredSlotCountsTowardsExhaustion()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 1, maxEntities: 1))
        {
            _ = table.Allocate();
            table.Place(0, archetype: 0, chunk: 0, row: 0);
            table.SetGenerationForTesting(0, uint.MaxValue);
            table.Free(0);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => table.Allocate());

            Assert.Contains("WorldOptions.MaxEntities", exception.Message, StringComparison.Ordinal);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Free_BelowMaxGeneration_AdvancesGenerationAndQueuesSlot()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 100))
        {
            _ = table.Allocate();
            table.Place(0, archetype: 3, chunk: 1, row: 2);
            table.SetGenerationForTesting(0, uint.MaxValue - 1);

            table.Free(0);

            EntityRecord record = table.GetRecord(0);
            Assert.Equal(EntityRecord.FreeState, record.Archetype);
            Assert.Equal(uint.MaxValue, record.Generation);
            Assert.Equal(0, table.RetiredCount);
            Assert.Equal(1, table.FreeCount);
            Assert.Equal(new Entity(0, uint.MaxValue), table.Allocate());
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Exhaustion_ThrowsNamingOption()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 2, maxEntities: 3))
        {
            for (int i = 0; i < 3; i++)
            {
                _ = table.Allocate();
                table.Place(i, archetype: 0, chunk: 0, row: i);
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => table.Allocate());

            Assert.Contains("WorldOptions.MaxEntities", exception.Message, StringComparison.Ordinal);
            Assert.Contains("3", exception.Message, StringComparison.Ordinal);
            Assert.Equal(3, table.SlotCount);
            Assert.Equal(3, table.Capacity);

            // A freed slot makes room again.
            table.Free(1);
            Assert.Equal(new Entity(1, 2), table.Allocate());
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Exhaustion_CountsAllocatedUnplacedSlots()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 1, maxEntities: 2))
        {
            _ = table.Allocate();
            _ = table.Allocate();

            _ = Assert.Throws<InvalidOperationException>(() => table.Allocate());
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Growth_AndDispose_NoLeaks()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "EntityTable");
        Storage.EntityTable table = new(tracking, initialCapacity: 2, maxEntities: 5);

        // One block for the records and one for the free-index ring.
        Assert.Equal(2, tracking.LiveAllocationCount);
        Assert.Equal(2, table.Capacity);

        int[] expectedCapacities = [2, 2, 4, 4, 5];
        for (int i = 0; i < 5; i++)
        {
            _ = table.Allocate();
            table.Place(i, archetype: i, chunk: 10 + i, row: 20 + i);

            // Growth doubles, capped at the maximum, and frees the old blocks: still exactly two live blocks.
            Assert.Equal(expectedCapacities[i], table.Capacity);
            Assert.Equal(2, tracking.LiveAllocationCount);
        }

        // Every record survived both growths.
        for (int i = 0; i < 5; i++)
        {
            EntityRecord record = table.GetRecord(i);
            Assert.Equal(1u, record.Generation);
            Assert.Equal(i, record.Archetype);
            Assert.Equal(10 + i, record.Chunk);
            Assert.Equal(20 + i, record.Row);
        }

        table.Dispose();
        tracking.ThrowIfLeaks();

        // A second Dispose frees nothing twice (the tracking allocator throws on a double free).
        table.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Growth_AllocatorFails_LeavesTableUnchanged()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "EntityTable");
        FailingAllocator failing = new(tracking);
        using (Storage.EntityTable table = new(failing, initialCapacity: 1, maxEntities: 8))
        {
            _ = table.Allocate();
            table.Place(0, archetype: 4, chunk: 5, row: 6);

            // Fail the first new block, then the second, so both growth allocations fail once.
            for (int failAt = 0; failAt < 2; failAt++)
            {
                failing.FailAfter = failAt;
                _ = Assert.Throws<InsufficientMemoryException>(() => table.Allocate());
                failing.FailAfter = -1;

                Assert.Equal(1, table.Capacity);
                Assert.Equal(1, table.SlotCount);
                Assert.Equal(new EntityRecord { Generation = 1, Archetype = 4, Chunk = 5, Row = 6 }, table.GetRecord(0));
            }

            Assert.Equal(new Entity(1, 1), table.Allocate());
            Assert.Equal(2, table.Capacity);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Constructor_AllocatorFails_FreesWhatItAllocated()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "EntityTable");
        FailingAllocator failing = new(tracking) { FailAfter = 1 };

        _ = Assert.Throws<InsufficientMemoryException>(() => new Storage.EntityTable(failing, initialCapacity: 4, maxEntities: 8));

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Constructor_InvalidArguments_Throw()
    {
        TrackingAllocator tracking = NewTracking();

        _ = Assert.Throws<ArgumentNullException>(() => new Storage.EntityTable(null!, 1, 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new Storage.EntityTable(tracking, 1, 0));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new Storage.EntityTable(tracking, 0, 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new Storage.EntityTable(tracking, 2, 1));

        // Arguments are checked before anything is allocated.
        Assert.Equal(0, tracking.TotalAllocationCount);
    }

    [Fact]
    public void Allocate_MarksSlotPendingUntilPlaced()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 2, maxEntities: 2))
        {
            _ = table.Allocate();

            Assert.Equal(new EntityRecord { Generation = 1, Archetype = EntityRecord.PendingState, Chunk = -1, Row = -1 }, table.GetRecord(0));
            Assert.Equal(0, table.PlacedCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Place_WritesLocationAndCountsPlaced()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 2, maxEntities: 2))
        {
            _ = table.Allocate();

            table.Place(0, archetype: 7, chunk: 8, row: 9);

            Assert.Equal(new EntityRecord { Generation = 1, Archetype = 7, Chunk = 8, Row = 9 }, table.GetRecord(0));
            Assert.Equal(1, table.PlacedCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Place_PlacedSlot_MovesToOtherArchetype()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 2, maxEntities: 2))
        {
            _ = table.Allocate();
            table.Place(0, archetype: 7, chunk: 8, row: 9);

            table.Place(0, archetype: 1, chunk: 2, row: 3);

            Assert.Equal(new EntityRecord { Generation = 1, Archetype = 1, Chunk = 2, Row = 3 }, table.GetRecord(0));
            Assert.Equal(1, table.PlacedCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Move_UpdatesChunkAndRowOnly()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 2, maxEntities: 2))
        {
            _ = table.Allocate();
            table.Place(0, archetype: 7, chunk: 8, row: 9);

            table.Move(0, chunk: 0, row: 1);

            Assert.Equal(new EntityRecord { Generation = 1, Archetype = 7, Chunk = 0, Row = 1 }, table.GetRecord(0));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Free_CountsAndClearsLocation()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 2, maxEntities: 2))
        {
            _ = table.Allocate();
            table.Place(0, archetype: 7, chunk: 8, row: 9);

            table.Free(0);

            Assert.Equal(new EntityRecord { Generation = 2, Archetype = EntityRecord.FreeState, Chunk = -1, Row = -1 }, table.GetRecord(0));
            Assert.Equal(0, table.PlacedCount);
            Assert.Equal(1, table.FreeCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Place_SlotNotInUse_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4))
        {
            _ = table.Allocate();
            table.Place(0, archetype: 0, chunk: 0, row: 0);
            table.Free(0);

            _ = Assert.Throws<InvalidOperationException>(() => table.Place(0, archetype: 0, chunk: 0, row: 0));
            Assert.Equal(EntityRecord.FreeState, table.GetRecord(0).Archetype);
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 0, -1)]
    public void Place_OutOfRange_Throws(int index, int archetype, int chunk, int row)
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4))
        {
            _ = table.Allocate();

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => table.Place(index, archetype, chunk, row));
            Assert.Equal(EntityRecord.PendingState, table.GetRecord(0).Archetype);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Move_SlotNotPlaced_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4))
        {
            _ = table.Allocate();

            _ = Assert.Throws<InvalidOperationException>(() => table.Move(0, chunk: 0, row: 0));
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, -1)]
    public void Move_OutOfRange_Throws(int index, int chunk, int row)
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4))
        {
            _ = table.Allocate();
            table.Place(0, archetype: 0, chunk: 0, row: 0);

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => table.Move(index, chunk, row));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Free_SlotNotPlaced_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4))
        {
            _ = table.Allocate();
            _ = table.Allocate();
            table.Place(1, archetype: 0, chunk: 0, row: 0);
            table.Free(1);

            // Pending (allocated, not placed) and already-free slots are both rejected, and nothing changes.
            _ = Assert.Throws<InvalidOperationException>(() => table.Free(0));
            _ = Assert.Throws<InvalidOperationException>(() => table.Free(1));
            Assert.Equal(1, table.FreeCount);
            Assert.Equal(2u, table.GetRecord(1).Generation);
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Free_OutOfRange_Throws(int index)
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4))
        {
            _ = table.Allocate();

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => table.Free(index));
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void GetRecord_OutOfRange_Throws(int index)
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4))
        {
            _ = table.Allocate();

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => table.GetRecord(index));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void SetGenerationForTesting_InvalidArguments_Throw()
    {
        TrackingAllocator tracking = NewTracking();
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4))
        {
            _ = table.Allocate();

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => table.SetGenerationForTesting(1, 5));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => table.SetGenerationForTesting(0, 0));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AfterDispose_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4);
        _ = table.Allocate();
        table.Dispose();
        tracking.ThrowIfLeaks();

        _ = Assert.Throws<ObjectDisposedException>(() => table.Allocate());
        _ = Assert.Throws<ObjectDisposedException>(() => table.Place(0, 0, 0, 0));
        _ = Assert.Throws<ObjectDisposedException>(() => table.Move(0, 0, 0));
        _ = Assert.Throws<ObjectDisposedException>(() => table.Free(0));
        _ = Assert.Throws<ObjectDisposedException>(() => table.GetRecord(0));

        // The rejected calls allocated nothing.
        tracking.ThrowIfLeaks();
    }

    /// <summary>A fresh tracking allocator per test, so each test's leak check sees only its own blocks.</summary>
    private static TrackingAllocator NewTracking() => new(new HeapAllocator(), "EntityTable");

    /// <summary>Passes calls through, but throws <see cref="InsufficientMemoryException"/> (an out-of-memory exception) once a set number of allocations succeed.</summary>
    private sealed unsafe class FailingAllocator(INativeAllocator inner) : INativeAllocator
    {
        /// <summary>Allocations that still succeed before one fails; -1 never fails.</summary>
        public int FailAfter { get; set; } = -1;

        public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            if (FailAfter == 0)
            {
                FailAfter = -1;
                throw new InsufficientMemoryException("Injected allocation failure.");
            }

            if (FailAfter > 0)
            {
                FailAfter--;
            }

            return inner.Allocate(byteCount, alignment, callerFile, callerLine);
        }

        public void Free(void* memory) => inner.Free(memory);
    }
}
