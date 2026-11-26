using System;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using Xunit;

namespace BrutalEngine.World.Tests.Entities;

/// <summary>Spawning and despawning entities through the world's direct API.</summary>
public sealed class SpawnTests
{
    [Fact]
    public void Spawn_IsAlive_DespawnMakesStale()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Assert.Equal(0, world.EntityCount);

            Entity first = world.Spawn();
            Entity second = world.Spawn();

            // Fresh slots in index order, at generation 1, alive and stored in the empty archetype.
            Assert.Equal(new Entity(0, 1), first);
            Assert.Equal(new Entity(1, 1), second);
            Assert.True(world.IsAlive(first));
            Assert.True(world.IsAlive(second));
            Assert.Equal(2, world.EntityCount);
            Assert.Equal(2, world.Archetypes.Empty.RowCount);
            Assert.Equal(1, world.ChunkCount);
            Assert.Equal(1, world.ArchetypeCount);

            world.Despawn(first);

            Assert.False(world.IsAlive(first));
            Assert.False(world.IsPending(first));
            Assert.True(world.IsAlive(second));
            Assert.Equal(1, world.EntityCount);
            Assert.Equal(1, world.Archetypes.Empty.RowCount);
            StaleHandleException stale = Assert.Throws<StaleHandleException>(() => world.Despawn(first));
            Assert.Equal(new Handle(first.Index, first.Generation), stale.Handle);

            // The freed slot is reused with an advanced generation, so the old entity stays stale.
            Entity reused = world.Spawn();
            Assert.Equal(new Entity(0, 2), reused);
            Assert.False(world.IsAlive(first));

            world.Despawn(second);
            world.Despawn(reused);

            // The emptied chunk went back to the pool.
            Assert.Equal(0, world.EntityCount);
            Assert.Equal(0, world.ChunkCount);
            Assert.Equal(1, world.ChunkPool.PooledCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Despawn_MidChunk_MovesLastRow()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Archetype empty = world.Archetypes.Empty;
            Entity a = world.Spawn();
            Entity b = world.Spawn();
            Entity c = world.Spawn();

            world.Despawn(a);

            // The last row (c) moved into a's row, and c's record says so; b didn't move.
            AssertLocation(world, c, chunk: 0, row: 0);
            AssertLocation(world, b, chunk: 0, row: 1);
            Assert.Equal(c, EntityTestSupport.EntityAt(empty, 0, 0));
            Assert.Equal(b, EntityTestSupport.EntityAt(empty, 0, 1));
            Assert.Equal(2, empty.RowCount);

            // Despawning the last row moves nothing.
            world.Despawn(b);
            AssertLocation(world, c, chunk: 0, row: 0);
            Assert.Equal(1, empty.RowCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Despawn_FromFullFirstChunk_MovesRowOfLastChunk()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Archetype empty = world.Archetypes.Empty;
            int capacity = empty.Layout.Capacity;
            var entities = new Entity[capacity + 1];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = world.Spawn();
            }

            Assert.Equal(2, world.ChunkCount);
            Entity last = entities[capacity];
            AssertLocation(world, last, chunk: 1, row: 0);

            world.Despawn(entities[5]);

            // The only row of the second chunk moved into the first, and the emptied chunk went back to the pool.
            AssertLocation(world, last, chunk: 0, row: 5);
            Assert.Equal(last, EntityTestSupport.EntityAt(empty, 0, 5));
            Assert.Equal(1, world.ChunkCount);
            Assert.Equal(1, world.ChunkPool.PooledCount);
            Assert.Equal(capacity, world.EntityCount);
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// The new chunk's first allocation fails: the first chunk of the archetype, or the second once the first is full.
    /// The entity is rolled back with an advanced generation, and nothing else changes.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Spawn_AllocationFails_RollsBack(int fullChunks)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        FailingAllocator allocator = new(tracking);

        // Room for every entity up front, so the failing allocation is the chunk's, not the entity table's growth.
        using (EcsWorld world = EntityTestSupport.NewWorld(allocator, initialEntityCapacity: 4096))
        {
            Archetype empty = world.Archetypes.Empty;
            int rows = fullChunks * empty.Layout.Capacity;
            for (int i = 0; i < rows; i++)
            {
                _ = world.Spawn();
            }

            int slots = world.Entities.SlotCount;
            long liveBlocks = tracking.LiveAllocationCount;
            allocator.FailNext = true;

            _ = Assert.Throws<InsufficientMemoryException>(() => world.Spawn());

            // The injected failure fired, and nothing visible changed: no entity, row or chunk was added.
            Assert.False(allocator.FailNext);
            Assert.Equal(rows, world.EntityCount);
            Assert.Equal(rows, empty.RowCount);
            Assert.Equal(fullChunks, world.ChunkCount);
            Assert.Equal(liveBlocks, tracking.LiveAllocationCount);
            Assert.Equal(0, world.Entities.PendingCount);
            Assert.False(world.IsAlive(new Entity((uint)rows, 1)));
            Assert.False(world.IsPending(new Entity((uint)rows, 1)));

            // The slot that was taken is released with an advanced generation, at the back of the reuse queue.
            Assert.Equal(slots + 1, world.Entities.SlotCount);
            Assert.Equal(1, world.Entities.FreeCount);

            // Still usable: the next spawn reuses that slot and opens the chunk.
            Entity next = world.Spawn();
            Assert.Equal(new Entity((uint)rows, 2), next);
            Assert.True(world.IsAlive(next));
            AssertLocation(world, next, chunk: fullChunks, row: 0);
            Assert.Equal(fullChunks + 1, world.ChunkCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Spawn_AllocationFails_RolledBackSlotGoesToBackOfQueue()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        FailingAllocator allocator = new(tracking);
        using (EcsWorld world = EntityTestSupport.NewWorld(allocator))
        {
            // Two freed slots in the queue (1, then 0), and no chunk, so the next spawn needs a new one.
            Entity a = world.Spawn();
            Entity b = world.Spawn();
            world.Despawn(b);
            world.Despawn(a);
            Assert.Equal(0, world.ChunkCount);
            world.TrimMemory();
            allocator.FailNext = true;

            _ = Assert.Throws<InsufficientMemoryException>(() => world.Spawn());

            // The failed spawn took slot 1 and released it at generation 3 behind slot 0.
            Assert.Equal(new Entity(0, 2), world.Spawn());
            Assert.Equal(new Entity(1, 3), world.Spawn());
            Assert.Equal(2, world.EntityCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Spawn_EntityTableGrowthFails_LeavesWorldUnchanged()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        FailingAllocator allocator = new(tracking);
        using (EcsWorld world = EntityTestSupport.NewWorld(allocator, initialEntityCapacity: 1))
        {
            Entity first = world.Spawn();
            allocator.FailNext = true;

            // The table is full, so the spawn grows it first; the growth fails before any slot is taken.
            _ = Assert.Throws<InsufficientMemoryException>(() => world.Spawn());

            Assert.False(allocator.FailNext);
            Assert.Equal(1, world.EntityCount);
            Assert.Equal(1, world.Entities.SlotCount);
            Assert.Equal(0, world.Entities.FreeCount);
            Assert.Equal(1, world.Archetypes.Empty.RowCount);
            Assert.True(world.IsAlive(first));

            Assert.Equal(new Entity(1, 1), world.Spawn());
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Spawn_MaxEntitiesExhausted_ThrowsAndLeavesWorldUnchanged()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking, initialEntityCapacity: 2, maxEntities: 2))
        {
            Entity a = world.Spawn();
            Entity b = world.Spawn();

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => world.Spawn());

            Assert.Contains("WorldOptions.MaxEntities", exception.Message, StringComparison.Ordinal);
            Assert.Equal(2, world.EntityCount);
            Assert.Equal(2, world.Archetypes.Empty.RowCount);
            Assert.Equal(0, world.Entities.PendingCount);
            Assert.True(world.IsAlive(a));
            Assert.True(world.IsAlive(b));

            // A despawn frees a slot for the next spawn.
            world.Despawn(a);
            Assert.Equal(new Entity(0, 2), world.Spawn());
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Despawn_LastGeneration_RetiresSlot()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity entity = world.Spawn();
            world.Entities.SetGenerationForTesting((int)entity.Index, uint.MaxValue);
            var last = new Entity(entity.Index, uint.MaxValue);

            world.Despawn(last);

            // Retired, never reused: the next spawn takes a fresh slot.
            Assert.Equal(1, world.Entities.RetiredCount);
            _ = Assert.Throws<StaleHandleException>(() => world.Despawn(last));
            Assert.Equal(new Entity(1, 1), world.Spawn());
        }

        tracking.ThrowIfLeaks();
    }

    private static void AssertLocation(EcsWorld world, Entity entity, int chunk, int row)
    {
        Assert.True(world.Entities.TryGetLocation(entity, out EntityRecord record));
        Assert.Equal(0, record.Archetype);
        Assert.Equal(chunk, record.Chunk);
        Assert.Equal(row, record.Row);
    }
}
