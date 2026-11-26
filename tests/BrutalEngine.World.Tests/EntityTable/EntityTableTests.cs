using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using CsCheck;
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

    [Fact]
    public void Pending_States()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "EntityTable");
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4))
        {
            Entity reserved = table.Reserve();

            Assert.Equal(new Entity(0, 1), reserved);
            Assert.True(table.IsPending(reserved));
            Assert.False(table.IsAlive(reserved));
            Assert.False(table.TryGetLocation(reserved, out EntityRecord location));
            Assert.Equal(default(EntityRecord), location);
            Assert.Equal(1, table.PendingCount);

            // Playback places it: alive, no longer pending.
            table.Place(0, archetype: 2, chunk: 3, row: 4);
            Assert.False(table.IsPending(reserved));
            Assert.True(table.IsAlive(reserved));
            Assert.True(table.TryGetLocation(reserved, out location));
            Assert.Equal(new EntityRecord { Generation = 1, Archetype = 2, Chunk = 3, Row = 4 }, location);

            // Cancelling (a cleared buffer) frees it with an advanced generation: stale, and the slot is reused next.
            Entity cancelled = table.Reserve();
            table.CancelPending(cancelled);
            Assert.False(table.IsPending(cancelled));
            Assert.False(table.IsAlive(cancelled));
            Assert.Equal(0, table.PendingCount);
            Assert.Equal(new EntityRecord { Generation = 2, Archetype = EntityRecord.FreeState, Chunk = -1, Row = -1 }, table.GetRecord(1));
            Assert.Equal(new Entity(1, 2), table.Reserve());
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void CancelPending_AtMaxGeneration_Retires()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "EntityTable");
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4))
        {
            _ = table.Reserve();
            table.SetGenerationForTesting(0, uint.MaxValue);

            table.CancelPending(new Entity(0, uint.MaxValue));

            Assert.Equal(EntityRecord.RetiredState, table.GetRecord(0).Archetype);
            Assert.Equal(1, table.RetiredCount);
            Assert.Equal(new Entity(1, 1), table.Reserve());
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void CancelPending_NotPending_Throws()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "EntityTable");
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4))
        {
            Entity placed = table.Allocate();
            table.Place(0, archetype: 0, chunk: 0, row: 0);
            Entity pending = table.Reserve();
            Entity freed = table.Allocate();
            table.Place(2, archetype: 0, chunk: 0, row: 1);
            _ = table.Allocate();
            table.Place(3, archetype: 0, chunk: 0, row: 2);
            table.Free(2);
            table.SetGenerationForTesting(3, uint.MaxValue);
            table.Free(3);
            var retired = new Entity(3, uint.MaxValue);

            Assert.Equal("entity", Assert.Throws<ArgumentException>(() => table.CancelPending(Entity.Null)).ParamName);
            _ = Assert.Throws<InvalidOperationException>(() => table.CancelPending(placed));
            Entity[] stale = [new Entity(pending.Index, 2), new Entity(7, 1), freed, new Entity(freed.Index, freed.Generation + 1), retired];
            foreach (Entity entity in stale)
            {
                StaleHandleException exception = Assert.Throws<StaleHandleException>(() => table.CancelPending(entity));
                Assert.Equal(new Handle(entity.Index, entity.Generation), exception.Handle);
            }

            // Nothing changed.
            Assert.True(table.IsAlive(placed));
            Assert.True(table.IsPending(pending));
            Assert.Equal(new EntityRecord { Generation = 2, Archetype = EntityRecord.FreeState, Chunk = -1, Row = -1 }, table.GetRecord(2));
            Assert.Equal(EntityRecord.RetiredState, table.GetRecord(3).Archetype);
            Assert.Equal(1, table.FreeCount);
            Assert.Equal(1, table.RetiredCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Exhaustion_CountsPending()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "EntityTable");
        using (Storage.EntityTable table = new(tracking, initialCapacity: 1, maxEntities: 2))
        {
            Entity first = table.Reserve();
            _ = table.Reserve();

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => table.Allocate());
            Assert.Contains("WorldOptions.MaxEntities", exception.Message, StringComparison.Ordinal);
            _ = Assert.Throws<InvalidOperationException>(() => table.Reserve());

            table.CancelPending(first);
            Assert.Equal(new Entity(0, 2), table.Reserve());
        }

        // Disposing with pending slots still frees everything.
        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData("null", typeof(ArgumentException))]
    [InlineData("stale", typeof(StaleHandleException))]
    [InlineData("retired", typeof(StaleHandleException))]
    [InlineData("pending", typeof(InvalidOperationException))]
    [InlineData("foreign-out-of-range", typeof(StaleHandleException))]
    [InlineData("unissued-generation", typeof(StaleHandleException))]
    [InlineData("alive", null)]
    public void Validate_Table(string situation, Type? expected)
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "EntityTable");
        Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4);
        Entity alive = table.Allocate();
        table.Place(0, archetype: 5, chunk: 6, row: 7);
        Entity stale = table.Allocate();
        table.Place(1, archetype: 0, chunk: 0, row: 0);
        _ = table.Allocate();
        table.Place(2, archetype: 0, chunk: 0, row: 0);
        Entity pending = table.Reserve();
        table.Free(1);
        table.SetGenerationForTesting(2, uint.MaxValue);
        table.Free(2);
        var retired = new Entity(2, uint.MaxValue);
        Entity entity = situation switch
        {
            "null" => Entity.Null,
            "stale" => stale,
            "retired" => retired,
            "pending" => pending,
            "foreign-out-of-range" => new Entity(100, 1),
            "unissued-generation" => new Entity(stale.Index, stale.Generation + 1),
            "alive" => alive,
            _ => throw new ArgumentOutOfRangeException(nameof(situation), situation, "Unknown situation."),
        };

        Exception? exception = Record.Exception(() => table.Validate(entity));
        EntityRecord record = expected is null ? table.Validate(entity) : default;
        table.Dispose();

        // Validation allocates nothing, and the table frees everything whichever way Validate went.
        tracking.ThrowIfLeaks();
        if (expected is null)
        {
            Assert.Null(exception);
            Assert.Equal(new EntityRecord { Generation = 1, Archetype = 5, Chunk = 6, Row = 7 }, record);
            return;
        }

        Assert.NotNull(exception);
        Assert.Equal(expected, exception.GetType());
        if (exception is StaleHandleException staleException)
        {
            Assert.Equal(new Handle(entity.Index, entity.Generation), staleException.Handle);
        }
        else if (exception is ArgumentException argumentException)
        {
            Assert.Equal("entity", argumentException.ParamName);
        }
        else
        {
            Assert.Contains(entity.ToString(), exception.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Lookups_AfterDispose_ReturnFalse()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "EntityTable");
        Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4);
        Entity alive = table.Allocate();
        table.Place(0, archetype: 0, chunk: 0, row: 0);
        table.Dispose();
        tracking.ThrowIfLeaks();

        Assert.False(table.IsAlive(alive));
        Assert.False(table.IsPending(alive));
        Assert.False(table.TryGetLocation(alive, out _));
        _ = Assert.Throws<ObjectDisposedException>(() => table.Reserve());
        _ = Assert.Throws<ObjectDisposedException>(() => table.CancelPending(alive));
        _ = Assert.Throws<ObjectDisposedException>(() => table.Validate(alive));

        // The lookups and the rejected calls after Dispose allocated nothing.
        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// Random reserve, allocate (and place), place, move, free, cancel and generation-seam sequences against a
    /// dictionary model with its own FIFO queue. After every step the counts agree, and every entity ever issued
    /// (plus each slot's current generation) gives the model's answer from every lookup; the touched entity is also
    /// classified by Validate.
    /// </summary>
    [Fact]
    public void Operations_MatchReferenceModel()
    {
        Gen.Select(Gen.Int[1, 12], Gen.Int[0, 7].Array[1, 200], Gen.Int.Array[200])
            .Sample((maxEntities, operations, picks) =>
            {
                // One tracker per sample: whatever the sample grew, reserved, cancelled or retired, Dispose frees it.
                TrackingAllocator tracking = new(new HeapAllocator(), "EntityTable");
                bool matches;
                using (Storage.EntityTable table = new(tracking, initialCapacity: 1 + (picks[0] & int.MaxValue) % maxEntities, maxEntities))
                {
                    matches = MatchesModel(table, maxEntities, operations, picks);
                }

                tracking.ThrowIfLeaks();
                return matches;
            });
    }

    private static bool MatchesModel(Storage.EntityTable table, int maxEntities, int[] operations, int[] picks)
    {
        Model model = new(maxEntities);
        List<Entity> issued = [];
        for (int step = 0; step < operations.Length; step++)
        {
            int pick = picks[step] & int.MaxValue;
            Entity touched = Entity.Null;
            switch (operations[step])
            {
                case 0:
                case 1:
                    bool reserve = operations[step] == 0;
                    Entity? expected = model.Take();
                    if (expected is null)
                    {
                        if (Record.Exception(() => _ = reserve ? table.Reserve() : table.Allocate()) is not InvalidOperationException)
                        {
                            return false;
                        }

                        break;
                    }

                    Entity entity = reserve ? table.Reserve() : table.Allocate();
                    if (entity != expected.Value)
                    {
                        return false;
                    }

                    issued.Add(entity);
                    touched = entity;
                    if (!reserve)
                    {
                        table.Place((int)entity.Index, pick % 5, pick % 7, pick % 11);
                        model.Place((int)entity.Index, pick % 5, pick % 7, pick % 11);
                    }

                    break;
                case 2 when model.TryPick(ModelState.Pending, pick, out int index):
                    table.Place(index, pick % 5, pick % 7, pick % 11);
                    model.Place(index, pick % 5, pick % 7, pick % 11);
                    touched = model.Current(index);
                    break;
                case 3 when model.TryPick(ModelState.Placed, pick, out int index):
                    table.Move(index, pick % 7, pick % 11);
                    model.Move(index, pick % 7, pick % 11);
                    touched = model.Current(index);
                    break;
                case 4 when model.TryPick(ModelState.Placed, pick, out int index):
                    touched = model.Current(index);
                    table.Free(index);
                    model.Release(index);
                    break;
                case 5 when model.TryPick(ModelState.Pending, pick, out int index):
                    touched = model.Current(index);
                    table.CancelPending(touched);
                    model.Release(index);
                    break;
                case 6 when model.SlotCount > 0:
                    int slot = pick % model.SlotCount;
                    uint generation = uint.MaxValue - (uint)(pick % 2);
                    if (model.Slots[slot].State is ModelState.Pending or ModelState.Placed)
                    {
                        table.SetGenerationForTesting(slot, generation);
                        model.Slots[slot].Generation = generation;
                        touched = model.Current(slot);
                    }

                    break;
                default:
                    touched = issued.Count > 0 && pick % 3 != 0 ? issued[pick % issued.Count] : new Entity((uint)(pick % 20), 1 + (uint)(pick % 3));
                    break;
            }

            if (!CountsMatch(table, model) || !LookupsMatch(table, model, issued) || !ValidateMatches(table, model, touched))
            {
                return false;
            }
        }

        return true;
    }

    [Fact]
    public void Lookups_DoNotAllocate()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "EntityTable");
        using (Storage.EntityTable table = new(tracking, initialCapacity: 4, maxEntities: 4))
        {
            Entity alive = table.Allocate();
            table.Place(0, archetype: 1, chunk: 2, row: 3);
            Entity pending = table.Reserve();
            Entity stale = table.Allocate();
            table.Place(2, archetype: 0, chunk: 0, row: 0);
            table.Free(2);
            Entity[] probes = [alive, pending, stale, Entity.Null, new Entity(1000, 1)];

            // Warm up so any one-time JIT or tiering work happens before measuring.
            int found = Probe(table, probes);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                found += Probe(table, probes);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(1001 * 3, found);

            static int Probe(Storage.EntityTable table, Entity[] probes)
            {
                int found = 0;
                foreach (Entity probe in probes)
                {
                    found += table.IsAlive(probe) ? 1 : 0;
                    found += table.IsPending(probe) ? 1 : 0;
                    found += table.TryGetLocation(probe, out EntityRecord record) && record.Row == 3 ? 1 : 0;
                }

                return found;
            }
        }

        tracking.ThrowIfLeaks();
    }

    private static bool CountsMatch(Storage.EntityTable table, Model model) =>
        table.SlotCount == model.SlotCount
        && table.PlacedCount == model.Count(ModelState.Placed)
        && table.PendingCount == model.Count(ModelState.Pending)
        && table.FreeCount == model.Count(ModelState.Free)
        && table.RetiredCount == model.Count(ModelState.Retired);

    private static bool LookupsMatch(Storage.EntityTable table, Model model, List<Entity> issued)
    {
        for (int slot = 0; slot < model.SlotCount; slot++)
        {
            if (!LookupMatches(table, model, model.Current(slot)))
            {
                return false;
            }
        }

        foreach (Entity entity in issued)
        {
            if (!LookupMatches(table, model, entity))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LookupMatches(Storage.EntityTable table, Model model, Entity entity)
    {
        ModelSlot? slot = model.Find(entity);
        bool alive = slot?.State == ModelState.Placed;
        bool located = table.TryGetLocation(entity, out EntityRecord record);
        return table.IsAlive(entity) == alive
            && table.IsPending(entity) == (slot?.State == ModelState.Pending)
            && located == alive
            && (!alive || (record.Generation == entity.Generation && record.Archetype == slot!.Archetype && record.Chunk == slot.Chunk && record.Row == slot.Row));
    }

    private static bool ValidateMatches(Storage.EntityTable table, Model model, Entity entity)
    {
        Type? expected = entity.IsNull
            ? typeof(ArgumentException)
            : model.Find(entity)?.State switch
            {
                ModelState.Placed => null,
                ModelState.Pending => typeof(InvalidOperationException),
                _ => typeof(StaleHandleException),
            };
        Exception? exception = Record.Exception(() => table.Validate(entity));
        return exception?.GetType() == expected;
    }

    private enum ModelState
    {
        Pending,
        Placed,
        Free,
        Retired,
    }

    private sealed class ModelSlot
    {
        public uint Generation { get; set; }

        public ModelState State { get; set; }

        public int Archetype { get; set; }

        public int Chunk { get; set; }

        public int Row { get; set; }
    }

    /// <summary>The entity-table rules written plainly: a dictionary of slots and a FIFO queue of free indices.</summary>
    private sealed class Model(int maxEntities)
    {
        private readonly Queue<int> _free = new();

        public Dictionary<int, ModelSlot> Slots { get; } = [];

        public int SlotCount => Slots.Count;

        public Entity? Take()
        {
            if (!_free.TryDequeue(out int index))
            {
                if (Slots.Count == maxEntities)
                {
                    return null;
                }

                index = Slots.Count;
                Slots[index] = new ModelSlot { Generation = 1 };
            }

            Slots[index].State = ModelState.Pending;
            return Current(index);
        }

        public void Place(int index, int archetype, int chunk, int row)
        {
            ModelSlot slot = Slots[index];
            (slot.State, slot.Archetype, slot.Chunk, slot.Row) = (ModelState.Placed, archetype, chunk, row);
        }

        public void Move(int index, int chunk, int row) => (Slots[index].Chunk, Slots[index].Row) = (chunk, row);

        public void Release(int index)
        {
            ModelSlot slot = Slots[index];
            if (slot.Generation == uint.MaxValue)
            {
                slot.State = ModelState.Retired;
                return;
            }

            slot.Generation++;
            slot.State = ModelState.Free;
            _free.Enqueue(index);
        }

        public Entity Current(int index) => new((uint)index, Slots[index].Generation);

        public ModelSlot? Find(Entity entity) =>
            !entity.IsNull && Slots.TryGetValue((int)entity.Index, out ModelSlot? slot) && slot.Generation == entity.Generation ? slot : null;

        public int Count(ModelState state)
        {
            int count = 0;
            foreach (ModelSlot slot in Slots.Values)
            {
                count += slot.State == state ? 1 : 0;
            }

            return count;
        }

        public bool TryPick(ModelState state, int pick, out int index)
        {
            int count = Count(state);
            index = -1;
            if (count == 0)
            {
                return false;
            }

            int target = pick % count;
            for (int i = 0; i < Slots.Count; i++)
            {
                if (Slots[i].State == state && target-- == 0)
                {
                    index = i;
                    return true;
                }
            }

            return false;
        }
    }

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
