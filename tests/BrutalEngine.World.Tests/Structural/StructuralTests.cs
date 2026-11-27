using System;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using Xunit;
using static BrutalEngine.World.Tests.Structural.StructuralSupport;

namespace BrutalEngine.World.Tests.Structural;

/// <summary>
/// Adding components to live entities (docs/design/world.md, sections 2 and 4, "Moves"): the entity moves to the
/// archetype with the component, keeps its other values, and the archetypes stay dense.
/// </summary>
public sealed class StructuralTests
{
    /// <summary>The misuse a row of <see cref="Add_Misuse_Table"/> makes.</summary>
    public enum Misuse
    {
        Null, // Entity.Null.
        Despawned, // A despawned entity.
        Retired, // An entity whose slot was retired at the last generation.
        OutOfRange, // An entity whose index is outside the world's table.
        Pending, // An entity reserved for a command buffer and not placed yet.
        Disposed, // An alive entity, after the world is disposed.
        WrongThread, // An alive entity, from a thread other than the owner.
        PresentData, // A data component the entity already has.
        PresentTag, // A tag the entity already has.
        TooWide, // A component whose target archetype's row is too wide for a chunk.
    }

    [Fact]
    public void Add_KeepsOtherValues()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // Position sorts before Health and Payload, so every Position add shifts the shared columns: a value
            // copied to the source's column index instead of the target's would land in the wrong column.
            RegisterInOrder(world);

            // A full first chunk and five rows in a second, all with Health and Payload.
            Expected expected = new();
            Entity first = world.Spawn(new Health(0), new Payload(0));
            int capacity = ArchetypeOf(world, first).Layout.Capacity;
            var entities = new Entity[capacity + 5];
            entities[0] = first;
            expected[first] = new Values(null, 0, 0, false);
            for (int i = 1; i < entities.Length; i++)
            {
                entities[i] = world.Spawn(new Health(i), new Payload(i));
                expected[entities[i]] = new Values(null, i, i, false);
            }

            Archetype source = ArchetypeOf(world, first);
            Assert.Equal(2, source.Chunks.ChunkCount);

            // Mid-chunk in the full first chunk: the source's last row fills the hole.
            Entity last = entities[^1];
            Add(world, expected, entities[3], new Position(3, -3));
            Assert.Equal((source.Index, 0, 3), Where(world, last));
            Archetype target = ArchetypeOf(world, entities[3]);
            Assert.NotEqual(source.Index, target.Index);
            Assert.Equal((target.Index, 0, 0), Where(world, entities[3]));

            // Mid-chunk in the last chunk, then the source's last row itself, which moves nothing in the source.
            Entity lastBefore = entities[^2];
            Add(world, expected, entities[capacity + 1], new Position(4, -4));
            Assert.Equal((source.Index, 1, 1), Where(world, lastBefore));
            Entity sourceLast = EntityTestSupport.EntityAt(source, 1, source.Chunks.GetCount(1) - 1);
            EntityRecord beforeLast = Location(world, entities[capacity]);
            Add(world, expected, sourceLast, new Position(5, -5));
            Assert.Equal(beforeLast.Row, Location(world, entities[capacity]).Row);

            // The first row of the last chunk.
            Entity lastNow = EntityTestSupport.EntityAt(source, 1, source.Chunks.GetCount(1) - 1);
            Add(world, expected, entities[capacity], new Position(8, -8));
            Assert.Equal((source.Index, 1, 0), Where(world, lastNow));

            // Down to one row in the last chunk: moving a first-chunk row empties it, and it goes back to the pool.
            Assert.Equal(1, source.Chunks.GetCount(1));
            int pooled = world.ChunkPool.PooledCount;
            Add(world, expected, entities[10], new Position(6, -6));
            Assert.Equal(1, source.Chunks.ChunkCount);
            Assert.Equal(pooled + 1, world.ChunkPool.PooledCount);

            // A tag moves an entity on from the target, keeping its three columns.
            world.Add(entities[3], default(Marker));
            expected[entities[3]] = expected[entities[3]] with { Marker = true };
            AssertDense(world);
            expected.AssertWorld(world);

            // Health into {Position, Payload}: the added id sorts between the shared ones, so Payload moves from
            // column 1 to column 2 while Position stays in column 0. The second entity fills the moved row.
            Entity withoutHealth = world.Spawn(new Position(9, -9), new Payload(9));
            Entity filler = world.Spawn(new Position(10, -10), new Payload(10));
            expected[withoutHealth] = new Values(new Position(9, -9), null, 9, false);
            expected[filler] = new Values(new Position(10, -10), null, 10, false);
            Archetype withoutHealthSource = ArchetypeOf(world, withoutHealth);
            world.Add(withoutHealth, new Health(11));
            expected[withoutHealth] = expected[withoutHealth] with { Health = 11 };
            Assert.Equal((withoutHealthSource.Index, 0, 0), Where(world, filler));
            Assert.Equal(target.Index, ArchetypeOf(world, withoutHealth).Index);
            AssertDense(world);
            expected.AssertWorld(world);

            // From the empty archetype (no columns), registering the type on first use.
            Entity bare = world.Spawn();
            expected[bare] = new Values(null, null, null, false);
            int registered = world.Components.Count;
            world.Add(bare, new Position(7, -7));
            expected[bare] = expected[bare] with { Position = new Position(7, -7) };
            Assert.Equal(registered, world.Components.Count);
            AssertDense(world);
            expected.AssertWorld(world);
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(Misuse.Null, typeof(ArgumentException))]
    [InlineData(Misuse.Despawned, typeof(StaleHandleException))]
    [InlineData(Misuse.Retired, typeof(StaleHandleException))]
    [InlineData(Misuse.OutOfRange, typeof(StaleHandleException))]
    [InlineData(Misuse.Pending, typeof(InvalidOperationException))]
    [InlineData(Misuse.Disposed, typeof(ObjectDisposedException))]
    [InlineData(Misuse.WrongThread, typeof(WrongThreadException))]
    [InlineData(Misuse.PresentData, typeof(InvalidOperationException))]
    [InlineData(Misuse.PresentTag, typeof(InvalidOperationException))]
    [InlineData(Misuse.TooWide, typeof(InvalidOperationException))]
    public void Add_Misuse_Table(Misuse misuse, Type expected)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        try
        {
            Entity alive = world.Spawn(new Position(1, 2), new Health(3), default(Marker));
            Entity other = world.Spawn(new Position(4, 5), new Health(6), default(Marker));
            Entity despawned = world.Spawn();
            world.Despawn(despawned);
            Entity retiring = world.Spawn();
            world.Entities.SetGenerationForTesting((int)retiring.Index, uint.MaxValue);
            var retired = new Entity(retiring.Index, uint.MaxValue);
            world.Despawn(retired);
            Entity pending = world.Entities.Reserve();
            world.SetChangeTickForTesting(9);
            Entity target = misuse switch
            {
                Misuse.Null => Entity.Null,
                Misuse.Despawned => despawned,
                Misuse.Retired => retired,
                Misuse.OutOfRange => new Entity(4096, 1),
                Misuse.Pending => pending,
                _ => alive,
            };

            // The entity rows add a type no test registers, so a registration before the entity check would show.
            Action call = misuse switch
            {
                Misuse.PresentData => () => world.Add(target, new Health(99)),
                Misuse.PresentTag => () => world.Add(target, default(Marker)),
                Misuse.TooWide => () => world.Add(target, new Huge(1)),
                _ => () => world.Add(target, new Unused(5)),
            };

            string before = Dump(world);
            int slots = world.Entities.SlotCount;
            int types = world.Components.Count;
            Exception? failure;
            if (misuse == Misuse.WrongThread)
            {
                failure = EntityTestSupport.RunOnOtherThread(call);
                Assert.Equal("EcsWorld.Add", Assert.IsType<WrongThreadException>(failure).ApiName);
            }
            else
            {
                if (misuse == Misuse.Disposed)
                {
                    world.Dispose();
                }

                failure = Record.Exception(call);
            }

            Assert.NotNull(failure);
            Assert.IsType(expected, failure);
            switch (misuse)
            {
                case Misuse.PresentData:
                    AssertNames(failure, alive, "Health", "already has", "Set");
                    break;
                case Misuse.PresentTag:
                    AssertNames(failure, alive, "Marker", "already has", "Set");
                    break;
                case Misuse.TooWide:
                    AssertNames(failure, null, "Huge", "rows in a chunk");
                    break;
            }

            if (misuse != Misuse.Disposed)
            {
                // Thrown before any effect: the same storage, ticks, slots and entities. Only a type that is new and
                // valid stays registered (registrations are permanent), as the too-wide row's does.
                Assert.Equal(before, Dump(world));
                Assert.Equal(slots, world.Entities.SlotCount);
                Assert.Equal(misuse == Misuse.TooWide ? types + 1 : types, world.Components.Count);
                Assert.True(world.IsPending(pending));
                Assert.Equal(new Position(1, 2), world.Get<Position>(alive));
                Assert.Equal(3, world.Get<Health>(alive).Value);
                Assert.Equal(new Position(4, 5), world.Get<Position>(other));

                // Still usable.
                world.Add(alive, new Payload(7));
                Assert.True(EntityComponents.Same(new Payload(7), world.Get<Payload>(alive)));
                Assert.Equal(3, world.Get<Health>(alive).Value);
                Assert.True(world.Has<Marker>(alive));
            }
        }
        finally
        {
            world.Dispose();
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// The target archetype's new chunk can't be allocated: its first chunk, or the next one when its last chunk is
    /// full. The entity stays in its source row with its values and stamps, nothing else changes, and the add then
    /// succeeds.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Add_AllocationFails_LeavesWorldUnchanged(bool targetHasFullChunk)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        FailingAllocator allocator = new(tracking);
        using (EcsWorld world = EntityTestSupport.NewWorld(allocator, initialEntityCapacity: 4096))
        {
            Entity[] sources = [world.Spawn(new Health(0), new Payload(0)), world.Spawn(new Health(1), new Payload(1)), world.Spawn(new Health(2), new Payload(2)), world.Spawn(new Health(3), new Payload(3))];

            // The first add creates the target archetype, caches the edge and opens the target's chunk arrays.
            world.Add(sources[0], new Position(0, 0));
            Archetype target = ArchetypeOf(world, sources[0]);
            if (targetHasFullChunk)
            {
                for (int i = 1; i < target.Layout.Capacity; i++)
                {
                    _ = world.Spawn(new Position(i, i), new Health(i), new Payload(i));
                }

                Assert.Equal(target.Layout.Capacity, target.Chunks.GetCount(0));
            }
            else
            {
                world.Despawn(sources[0]);
                Assert.Equal(0, target.Chunks.ChunkCount);
            }

            // An empty pool, so the target's new chunk needs the allocator.
            world.TrimMemory();
            world.SetChangeTickForTesting(9);
            string before = Dump(world);
            long liveBlocks = tracking.LiveAllocationCount;
            int pooled = world.ChunkPool.PooledCount;
            allocator.FailNext = true;

            // A row that isn't the source's last, so a swap-remove before the failure would show.
            _ = Assert.Throws<InsufficientMemoryException>(() => world.Add(sources[1], new Position(-1, -1)));

            Assert.False(allocator.FailNext);
            Assert.Equal(before, Dump(world));
            Assert.Equal(liveBlocks, tracking.LiveAllocationCount);
            Assert.Equal(pooled, world.ChunkPool.PooledCount);
            Assert.False(world.Has<Position>(sources[1]));
            Assert.Equal(1, world.Get<Health>(sources[1]).Value);
            Assert.Equal(0, world.Entities.PendingCount);
            AssertDense(world);

            // Still usable: the same add now succeeds and keeps the other values.
            world.Add(sources[1], new Position(-1, -1));
            Assert.Equal(new Position(-1, -1), world.Get<Position>(sources[1]));
            Assert.Equal(1, world.Get<Health>(sources[1]).Value);
            Assert.True(EntityComponents.Same(new Payload(1), world.Get<Payload>(sources[1])));
            Assert.Equal(targetHasFullChunk ? 2 : 1, target.Chunks.ChunkCount);
            AssertDense(world);
        }

        tracking.ThrowIfLeaks();
    }

    private static void Add(EcsWorld world, Expected expected, Entity entity, Position position)
    {
        world.Add(entity, position);
        expected[entity] = expected[entity] with { Position = position };
        AssertDense(world);
        expected.AssertWorld(world);
    }

    private static (int Archetype, int Chunk, int Row) Where(EcsWorld world, Entity entity)
    {
        EntityRecord record = Location(world, entity);
        return (record.Archetype, record.Chunk, record.Row);
    }

    private static void AssertNames(Exception failure, Entity? entity, string component, params string[] words)
    {
        if (entity is { } named)
        {
            Assert.Contains(named.ToString(), failure.Message, StringComparison.Ordinal);
        }

        Assert.Contains($"Entities.{component}", failure.Message, StringComparison.Ordinal);
        foreach (string word in words)
        {
            Assert.Contains(word, failure.Message, StringComparison.Ordinal);
        }
    }
}
