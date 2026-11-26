using System;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using Xunit;

namespace BrutalEngine.World.Tests.Entities;

/// <summary>Spawning and despawning entities, with and without components, through the world's direct API.</summary>
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

    /// <summary>
    /// Spawns with 0 to 4 components store each value, readable with Get and TryGet, in the archetype of exactly those
    /// components; Has is true for them and false for the rest. Two entities per arity, so the second row is checked too.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void SpawnWithComponents_ValuesReadable(int count)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity first = SpawnWith(world, count, seed: 10);
            Entity second = SpawnWith(world, count, seed: 20);

            Assert.Equal(2, world.EntityCount);
            Assert.Equal(count == 0 ? 1 : 2, world.ArchetypeCount);
            Assert.Equal(count, world.Components.Count);
            AssertValues(world, first, count, seed: 10);
            AssertValues(world, second, count, seed: 20);
            Assert.True(world.Entities.TryGetLocation(second, out EntityRecord record));
            Assert.Equal(1, record.Row);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void SpawnWithComponents_TypeOrder_SameArchetype()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity a = world.Spawn(new Position(1, 2), new Health(3), default(Marker));
            Entity b = world.Spawn(default(Marker), new Health(4), new Position(5, 6));

            Assert.Equal(2, world.ArchetypeCount);
            Assert.True(world.Entities.TryGetLocation(a, out EntityRecord ra));
            Assert.True(world.Entities.TryGetLocation(b, out EntityRecord rb));
            Assert.Equal(ra.Archetype, rb.Archetype);
            Assert.Equal(4, world.Get<Health>(b).Value);
            Assert.Equal(new Position(5, 6), world.Get<Position>(b));
            Assert.True(world.Has<Marker>(b));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Despawn_WithComponents_MovedRowKeepsValues()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // More than a chunk of Payload rows, so the last row comes from the second chunk.
            Archetype archetype = world.Archetypes.GetOrCreate([world.Components.Register<Payload>(), world.Components.Register<Health>()]);
            int capacity = archetype.Layout.Capacity;
            var entities = new Entity[capacity + 1];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = world.Spawn(new Payload(i * 100), new Health(i));
            }

            world.Despawn(entities[3]);

            Entity moved = entities[capacity];
            Assert.True(world.Entities.TryGetLocation(moved, out EntityRecord record));
            Assert.Equal(0, record.Chunk);
            Assert.Equal(3, record.Row);
            Assert.Equal(capacity, world.Get<Health>(moved).Value);
            Assert.True(EntityComponents.Same(new Payload(capacity * 100), world.Get<Payload>(moved)));
            Assert.Equal(4, world.Get<Health>(entities[4]).Value);
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// Every way to give a type twice, at each arity: the first check of the call, so the types aren't registered, no
    /// archetype is created and no slot is taken. The parameter named is the later occurrence.
    /// </summary>
    [Theory]
    [InlineData(2, "c2", "Position")]
    [InlineData(30, "c2", "Position")]
    [InlineData(31, "c3", "Position")]
    [InlineData(32, "c3", "Position")]
    [InlineData(40, "c2", "Health")]
    [InlineData(41, "c4", "Health")]
    [InlineData(42, "c4", "Health")]
    [InlineData(43, "c4", "Health")]
    [InlineData(44, "c3", "Health")]
    [InlineData(45, "c3", "Health")]
    public void Spawn_DuplicateType_ThrowsBeforeAnyEffect(int shape, string paramName, string typeName)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Action spawn = shape switch
            {
                2 => () => world.Spawn(new Position(1, 1), new Position(2, 2)),
                30 => () => world.Spawn(new Position(1, 1), new Position(2, 2), new Health(1)),
                40 => () => world.Spawn(new Health(1), new Health(2), new Payload(1), default(Marker)),
                45 => () => world.Spawn(new Health(1), new Payload(1), new Health(2), default(Marker)),
                31 => () => world.Spawn(new Position(1, 1), new Health(1), new Position(2, 2)),
                32 => () => world.Spawn(new Health(1), new Position(1, 1), new Position(2, 2)),
                41 => () => world.Spawn(new Health(1), new Payload(1), default(Marker), new Health(2)),
                42 => () => world.Spawn(new Payload(1), new Health(1), default(Marker), new Health(2)),
                43 => () => world.Spawn(new Payload(1), default(Marker), new Health(1), new Health(2)),
                _ => () => world.Spawn(new Payload(1), new Health(1), new Health(2), default(Marker)),
            };
            long allocations = tracking.TotalAllocationCount;

            ArgumentException exception = Assert.Throws<ArgumentException>(spawn);

            Assert.Equal(paramName, exception.ParamName);
            Assert.Contains($"Entities.{typeName} more than once", exception.Message, StringComparison.Ordinal);
            Assert.Equal(0, world.Components.Count);
            Assert.Equal(1, world.ArchetypeCount);
            Assert.Equal(0, world.Entities.SlotCount);
            Assert.Equal(allocations, tracking.TotalAllocationCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void SpawnWithComponents_RowTooWide_ThrowsBeforeTakingSlot()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => world.Spawn(new Huge(1)));

            // The registration stays (registrations are permanent); no archetype, slot or row was added.
            Assert.Contains(Huge.Info.FullName, exception.Message, StringComparison.Ordinal);
            Assert.True(world.Components.TryGetId<Huge>(out _));
            Assert.Equal(1, world.ArchetypeCount);
            Assert.Equal(0, world.Entities.SlotCount);
            Assert.Equal(0, world.EntityCount);
            Assert.Equal(new Entity(0, 1), world.Spawn(new Health(1)));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void SpawnWithComponents_AllocationFails_RollsBack()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        FailingAllocator allocator = new(tracking);
        using (EcsWorld world = EntityTestSupport.NewWorld(allocator))
        {
            Entity first = world.Spawn(new Health(7));
            allocator.FailNext = true;

            // A new archetype's first chunk fails: the archetype stays (archetypes are permanent), the entity doesn't.
            _ = Assert.Throws<InsufficientMemoryException>(() => world.Spawn(new Position(1, 2), new Health(3)));

            Assert.False(allocator.FailNext);
            Assert.Equal(3, world.ArchetypeCount);
            Assert.Equal(1, world.EntityCount);
            Assert.Equal(1, world.ChunkCount);
            Assert.Equal(0, world.Entities.PendingCount);
            Assert.Equal(7, world.Get<Health>(first).Value);

            Entity next = world.Spawn(new Position(4, 5), new Health(6));
            Assert.Equal(new Entity(1, 2), next);
            Assert.Equal(new Position(4, 5), world.Get<Position>(next));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void HasGetTryGet_ComponentRules()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity tagged = world.Spawn(new Health(5), default(Marker));
            Entity plain = world.Spawn(new Position(1, 2));
            int registered = world.Components.Count;

            // Has: true for data and tags the entity has; false for a registered type it lacks and for unregistered
            // types, which it doesn't register.
            Assert.True(world.Has<Health>(tagged));
            Assert.True(world.Has<Marker>(tagged));
            Assert.False(world.Has<Position>(tagged));
            Assert.False(world.Has<Marker>(plain));
            Assert.False(world.Has<Unused>(plain));
            Assert.False(world.Has<UnusedTag>(plain));
            Assert.Equal(registered, world.Components.Count);

            // TryGet: false and default for a missing or unregistered data type.
            Assert.True(world.TryGet(tagged, out Health health));
            Assert.Equal(5, health.Value);
            Assert.False(world.TryGet(plain, out Health missing));
            Assert.Equal(default(Health), missing);
            Assert.False(world.TryGet(plain, out Unused unused));
            Assert.Equal(default(Unused), unused);
            Assert.Equal(registered, world.Components.Count);
        }

        tracking.ThrowIfLeaks();
    }

    private static Entity SpawnWith(EcsWorld world, int count, int seed) => count switch
    {
        0 => world.Spawn(),
        1 => world.Spawn(new Position(seed, seed + 1)),
        2 => world.Spawn(new Position(seed, seed + 1), new Health(seed + 2)),
        3 => world.Spawn(new Position(seed, seed + 1), new Health(seed + 2), new Payload(seed + 3)),
        _ => world.Spawn(new Position(seed, seed + 1), new Health(seed + 2), new Payload(seed + 3), default(Marker)),
    };

    private static void AssertValues(EcsWorld world, Entity entity, int count, int seed)
    {
        Assert.True(world.IsAlive(entity));
        Assert.Equal(count >= 1, world.Has<Position>(entity));
        Assert.Equal(count >= 2, world.Has<Health>(entity));
        Assert.Equal(count >= 3, world.Has<Payload>(entity));
        Assert.Equal(count >= 4, world.Has<Marker>(entity));
        if (count >= 1)
        {
            Assert.Equal(new Position(seed, seed + 1), world.Get<Position>(entity));
            Assert.True(world.TryGet(entity, out Position position));
            Assert.Equal(new Position(seed, seed + 1), position);
        }

        if (count >= 2)
        {
            Assert.Equal(seed + 2, world.Get<Health>(entity).Value);
        }

        if (count >= 3)
        {
            Assert.True(EntityComponents.Same(new Payload(seed + 3), world.Get<Payload>(entity)));
        }
    }

    private static void AssertLocation(EcsWorld world, Entity entity, int chunk, int row)
    {
        Assert.True(world.Entities.TryGetLocation(entity, out EntityRecord record));
        Assert.Equal(0, record.Archetype);
        Assert.Equal(chunk, record.Chunk);
        Assert.Equal(row, record.Row);
    }
}
