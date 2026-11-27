using System;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using Xunit;
using static BrutalEngine.World.Tests.Structural.StructuralSupport;

namespace BrutalEngine.World.Tests.Structural;

/// <summary>
/// Adding, setting and removing components on live entities (docs/design/world.md, sections 2 and 4, "Moves"): the
/// entity moves to the archetype with or without the component, or a set replaces a present value in place, it keeps
/// its other values, and the archetypes stay dense.
/// </summary>
public sealed class StructuralTests
{
    /// <summary>
    /// The misuse a row of <see cref="Add_Misuse_Table"/>, <see cref="Remove_Misuse_Table"/> or
    /// <see cref="Set_Misuse_Table"/> makes. The remove table uses the entity and world rows only (a remove of a present
    /// component is the normal case, and of an absent one returns false); the set table adds the too-wide row (a set
    /// of a present component replaces it).
    /// </summary>
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

    [Fact]
    public void Remove_KeepsOtherValues()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // Position sorts before Health and Payload, so every Position remove shifts the shared columns: a value
            // copied to the source's column index instead of the target's would land in the wrong column.
            RegisterInOrder(world);

            // A full first chunk and five rows in a second, all with Position, Health and Payload.
            Expected expected = new();
            Entity first = world.Spawn(new Position(0, 0), new Health(0), new Payload(0));
            int capacity = ArchetypeOf(world, first).Layout.Capacity;
            var entities = new Entity[capacity + 5];
            entities[0] = first;
            expected[first] = new Values(new Position(0, 0), 0, 0, false);
            for (int i = 1; i < entities.Length; i++)
            {
                entities[i] = world.Spawn(new Position(i, -i), new Health(i), new Payload(i));
                expected[entities[i]] = new Values(new Position(i, -i), i, i, false);
            }

            Archetype source = ArchetypeOf(world, first);
            Assert.Equal(2, source.Chunks.ChunkCount);

            // Mid-chunk in the full first chunk: the source's last row fills the hole.
            Entity last = entities[^1];
            Remove<Position>(world, expected, entities[3]);
            Assert.Equal((source.Index, 0, 3), Where(world, last));
            Archetype target = ArchetypeOf(world, entities[3]);
            Assert.NotEqual(source.Index, target.Index);
            Assert.Equal((target.Index, 0, 0), Where(world, entities[3]));

            // Mid-chunk in the last chunk; the source's last row, which moves nothing in the source; the first row of
            // the last chunk.
            Remove<Position>(world, expected, entities[capacity + 1]);
            Assert.Equal((source.Index, 1, 1), Where(world, entities[^2]));
            Entity sourceLast = EntityTestSupport.EntityAt(source, 1, source.Chunks.GetCount(1) - 1);
            EntityRecord beforeLast = Location(world, entities[capacity]);
            Remove<Position>(world, expected, sourceLast);
            Assert.Equal(beforeLast.Row, Location(world, entities[capacity]).Row);
            Entity lastNow = EntityTestSupport.EntityAt(source, 1, source.Chunks.GetCount(1) - 1);
            Remove<Position>(world, expected, entities[capacity]);
            Assert.Equal((source.Index, 1, 0), Where(world, lastNow));

            // Down to one row in the last chunk: moving a first-chunk row empties it, and it goes back to the pool.
            Assert.Equal(1, source.Chunks.GetCount(1));
            int pooled = world.ChunkPool.PooledCount;
            Remove<Position>(world, expected, entities[10]);
            Assert.Equal(1, source.Chunks.ChunkCount);
            Assert.Equal(pooled + 1, world.ChunkPool.PooledCount);

            // The middle column, then the last component, down to the empty archetype (no columns).
            Remove<Health>(world, expected, entities[3]);
            Remove<Payload>(world, expected, entities[3]);
            Assert.Equal(0, Location(world, entities[3]).Archetype);

            // A tag.
            Entity tagged = world.Spawn(new Health(-1), default(Marker));
            expected[tagged] = new Values(null, -1, null, true);
            Remove<Marker>(world, expected, tagged);
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// An absent component: registered and not on the entity (data or tag), or not registered at all. Remove returns
    /// false and changes nothing: no move, no stamp, no registration. The entity's chunk keeps other rows, so a stamp
    /// or a move would show.
    /// </summary>
    [Theory]
    [InlineData("data")]
    [InlineData("tag")]
    [InlineData("unregistered")]
    [InlineData("unregistered tag")]
    public void Remove_Absent_ReturnsFalseWithoutEffect(string rule)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = world.Spawn(new Position(9, 9), new Payload(9), default(Marker));
            Entity first = world.Spawn(new Health(1));
            Entity middle = world.Spawn(new Health(2));
            _ = world.Spawn(new Health(3));
            world.SetChangeTickForTesting(5);
            string before = Dump(world);
            int types = world.Components.Count;

            bool removed = rule switch
            {
                "data" => world.Remove<Position>(middle),
                "tag" => world.Remove<Marker>(middle),
                "unregistered" => world.Remove<Unused>(middle),
                _ => world.Remove<UnusedTag>(middle),
            };

            Assert.False(removed);
            Assert.Equal(before, Dump(world));
            Assert.Equal(types, world.Components.Count);
            Assert.Equal(2, world.Get<Health>(middle).Value);
            Assert.Equal(1, world.Get<Health>(first).Value);
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
    public void Remove_Misuse_Table(Misuse misuse, Type expected)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        try
        {
            Entity alive = world.Spawn(new Position(1, 2), new Health(3), default(Marker));
            Entity other = world.Spawn(new Position(4, 5), new Health(6), default(Marker));
            Entity despawned = world.Spawn(new Position(0, 0));
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

            // A component the alive entity has, so a misordered check would move it; and an unregistered one, which the
            // entity check rejects as well before the absent component's early false.
            Action call = () => _ = world.Remove<Health>(target);
            Action callUnregistered = () => _ = world.Remove<Unused>(target);
            string before = Dump(world);
            int slots = world.Entities.SlotCount;
            int types = world.Components.Count;
            Exception? failure;
            if (misuse == Misuse.WrongThread)
            {
                failure = EntityTestSupport.RunOnOtherThread(call);
                Assert.Equal("EcsWorld.Remove", Assert.IsType<WrongThreadException>(failure).ApiName);
                Assert.IsType(expected, EntityTestSupport.RunOnOtherThread(callUnregistered));
            }
            else
            {
                if (misuse == Misuse.Disposed)
                {
                    world.Dispose();
                }

                failure = Record.Exception(call);
                Exception? unregisteredFailure = Record.Exception(callUnregistered);
                Assert.IsType(expected, unregisteredFailure);
                if (misuse == Misuse.Disposed)
                {
                    // The world's own check, not a disposed storage type further in.
                    Assert.Equal(typeof(EcsWorld).FullName, Assert.IsType<ObjectDisposedException>(unregisteredFailure).ObjectName);
                }
            }

            Assert.NotNull(failure);
            Assert.IsType(expected, failure);
            if (misuse == Misuse.Disposed)
            {
                Assert.Equal(typeof(EcsWorld).FullName, Assert.IsType<ObjectDisposedException>(failure).ObjectName);
            }
            else
            {
                Assert.Equal(before, Dump(world));
                AssertDense(world);
                Assert.Equal(slots, world.Entities.SlotCount);
                Assert.Equal(types, world.Components.Count);
                Assert.True(world.IsPending(pending));
                Assert.Equal(3, world.Get<Health>(alive).Value);
                Assert.Equal(new Position(4, 5), world.Get<Position>(other));

                // Still usable: removing Position, which sorts first, moves Health from column 1 to column 0.
                Assert.True(world.Remove<Position>(alive));
                Assert.Equal(3, world.Get<Health>(alive).Value);
                Assert.True(world.Has<Marker>(alive));
                Assert.Equal(6, world.Get<Health>(other).Value);
                AssertDense(world);
            }
        }
        finally
        {
            world.Dispose();
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// The target archetype's new chunk can't be allocated, its first or its next: the entity keeps the component,
    /// its row, values and stamps, nothing else changes, and the remove then succeeds.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Remove_AllocationFails_LeavesWorldUnchanged(bool targetHasFullChunk)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        FailingAllocator allocator = new(tracking);
        using (EcsWorld world = EntityTestSupport.NewWorld(allocator, initialEntityCapacity: 4096))
        {
            // Position sorts first, so the retried remove shifts Health and Payload by one column.
            RegisterInOrder(world);
            Entity[] sources = [world.Spawn(new Position(0, 0), new Health(0), new Payload(0)), world.Spawn(new Position(1, 1), new Health(1), new Payload(1)), world.Spawn(new Position(2, 2), new Health(2), new Payload(2)), world.Spawn(new Position(3, 3), new Health(3), new Payload(3))];

            // The first remove creates the target archetype, caches the edge and opens the target's chunk arrays.
            Assert.True(world.Remove<Position>(sources[0]));
            Archetype target = ArchetypeOf(world, sources[0]);
            if (targetHasFullChunk)
            {
                for (int i = 1; i < target.Layout.Capacity; i++)
                {
                    _ = world.Spawn(new Health(i), new Payload(i));
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
            _ = Assert.Throws<InsufficientMemoryException>(() => world.Remove<Position>(sources[1]));

            Assert.False(allocator.FailNext);
            Assert.Equal(before, Dump(world));
            AssertDense(world);
            Assert.Equal(liveBlocks, tracking.LiveAllocationCount);
            Assert.Equal(pooled, world.ChunkPool.PooledCount);
            Assert.Equal(new Position(1, 1), world.Get<Position>(sources[1]));
            Assert.Equal(1, world.Get<Health>(sources[1]).Value);
            Assert.Equal(0, world.Entities.PendingCount);

            // Still usable: the same remove now succeeds and keeps the other values.
            Assert.True(world.Remove<Position>(sources[1]));
            Assert.False(world.Has<Position>(sources[1]));
            Assert.Equal(1, world.Get<Health>(sources[1]).Value);
            Assert.True(EntityComponents.Same(new Payload(1), world.Get<Payload>(sources[1])));
            Assert.Equal(targetHasFullChunk ? 2 : 1, target.Chunks.ChunkCount);
            AssertDense(world);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Set_ReplaceAndAdd()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // Position sorts before Health and Payload, so a Set that adds Position shifts the shared columns.
            RegisterInOrder(world);

            // A full first chunk and three rows in a second, all with Position, Health and Payload.
            Expected expected = new();
            Entity first = world.Spawn(new Position(0, 0), new Health(0), new Payload(0));
            expected[first] = new Values(new Position(0, 0), 0, 0, false);
            int capacity = ArchetypeOf(world, first).Layout.Capacity;
            var entities = new Entity[capacity + 3];
            entities[0] = first;
            for (int i = 1; i < entities.Length; i++)
            {
                entities[i] = world.Spawn(new Position(i, -i), new Health(i), new Payload(i));
                expected[entities[i]] = new Values(new Position(i, -i), i, i, false);
            }

            // Present data, in each column and in both chunks: replaced in place, nothing moves.
            SetInPlace(world, expected, entities[4], new Health(100), values => values with { Health = 100 });
            SetInPlace(world, expected, entities[capacity + 1], new Position(7, 7), values => values with { Position = new Position(7, 7) });
            SetInPlace(world, expected, entities[capacity + 2], new Payload(-50), values => values with { Payload = -50 });

            // A present tag: nothing changes at all.
            Entity tagged = world.Spawn(new Health(-1), default(Marker));
            Entity taggedNext = world.Spawn(new Health(-2), default(Marker));
            expected[tagged] = new Values(null, -1, null, true);
            expected[taggedNext] = new Values(null, -2, null, true);
            string before = Dump(world);
            world.Set(tagged, default(Marker));
            Assert.Equal(before, Dump(world));
            expected.AssertWorld(world);

            // Absent data: moves like Add. Position comes first in the target, so Health and Payload shift by one
            // column, and the source's last row fills the hole.
            Entity withoutPosition = world.Spawn(new Health(20), new Payload(20));
            Entity filler = world.Spawn(new Health(21), new Payload(21));
            expected[withoutPosition] = new Values(null, 20, 20, false);
            expected[filler] = new Values(null, 21, 21, false);
            Archetype withoutPositionSource = ArchetypeOf(world, withoutPosition);
            world.Set(withoutPosition, new Position(20, -20));
            expected[withoutPosition] = expected[withoutPosition] with { Position = new Position(20, -20) };
            Assert.Equal(ArchetypeOf(world, first).Index, ArchetypeOf(world, withoutPosition).Index);
            Assert.Equal((withoutPositionSource.Index, 0, 0), Where(world, filler));
            AssertDense(world);
            expected.AssertWorld(world);

            // An absent tag: moves to the archetype with the tag, keeping all three columns.
            world.Set(entities[2], default(Marker));
            expected[entities[2]] = expected[entities[2]] with { Marker = true };
            Assert.NotEqual(ArchetypeOf(world, first).Index, ArchetypeOf(world, entities[2]).Index);
            AssertDense(world);
            expected.AssertWorld(world);

            // An unregistered type: registered on first use, then added.
            int types = world.Components.Count;
            world.Set(entities[5], new Unused(55));
            Assert.Equal(types + 1, world.Components.Count);
            Assert.Equal(55, world.Get<Unused>(entities[5]).Value);
            AssertDense(world);
            expected.AssertWorld(world);

            // Set again on the moved entity: now present, so replaced in place.
            SetInPlace(world, expected, withoutPosition, new Position(30, -30), values => values with { Position = new Position(30, -30) });
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
    [InlineData(Misuse.TooWide, typeof(InvalidOperationException))]
    public void Set_Misuse_Table(Misuse misuse, Type expected)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        try
        {
            Entity alive = world.Spawn(new Position(1, 2), new Health(3), default(Marker));
            Entity other = world.Spawn(new Position(4, 5), new Health(6), default(Marker));
            Entity despawned = world.Spawn(new Position(0, 0));
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

            // A component the alive entity has, so a misordered check would replace and stamp it; and a type no test
            // registers, so a registration or a move before the entity check would show.
            Action call = misuse == Misuse.TooWide ? () => world.Set(target, new Huge(1)) : () => world.Set(target, new Health(99));
            Action callUnregistered = () => world.Set(target, new Unused(5));
            string before = Dump(world);
            int slots = world.Entities.SlotCount;
            int types = world.Components.Count;
            Exception? failure;
            Exception? unregisteredFailure = null;
            if (misuse == Misuse.WrongThread)
            {
                failure = EntityTestSupport.RunOnOtherThread(call);
                Assert.Equal("EcsWorld.Set", Assert.IsType<WrongThreadException>(failure).ApiName);
                unregisteredFailure = EntityTestSupport.RunOnOtherThread(callUnregistered);
            }
            else
            {
                if (misuse == Misuse.Disposed)
                {
                    world.Dispose();
                }

                failure = Record.Exception(call);
                if (misuse != Misuse.TooWide)
                {
                    unregisteredFailure = Record.Exception(callUnregistered);
                }
            }

            Assert.NotNull(failure);
            Assert.IsType(expected, failure);
            if (misuse != Misuse.TooWide)
            {
                Assert.IsType(expected, unregisteredFailure);
            }

            if (misuse == Misuse.TooWide)
            {
                AssertNames(failure, null, "Huge", "rows in a chunk");
            }

            if (misuse == Misuse.Disposed)
            {
                Assert.Equal(typeof(EcsWorld).FullName, Assert.IsType<ObjectDisposedException>(failure).ObjectName);
            }
            else
            {
                // Thrown before any effect. Only the too-wide type, which is new and valid, stays registered.
                Assert.Equal(before, Dump(world));
                AssertDense(world);
                Assert.Equal(slots, world.Entities.SlotCount);
                Assert.Equal(misuse == Misuse.TooWide ? types + 1 : types, world.Components.Count);
                Assert.True(world.IsPending(pending));
                Assert.Equal(3, world.Get<Health>(alive).Value);
                Assert.Equal(new Position(4, 5), world.Get<Position>(other));

                // Still usable: a replace, then an add that moves the entity.
                world.Set(alive, new Health(30));
                world.Set(alive, new Payload(7));
                Assert.Equal(30, world.Get<Health>(alive).Value);
                Assert.True(EntityComponents.Same(new Payload(7), world.Get<Payload>(alive)));
                Assert.Equal(new Position(1, 2), world.Get<Position>(alive));
                Assert.True(world.Has<Marker>(alive));
                AssertDense(world);
            }
        }
        finally
        {
            world.Dispose();
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// A Set that adds, whose target archetype's new chunk can't be allocated: the entity stays where it was with its
    /// values and stamps, nothing else changes, and the Set then succeeds.
    /// </summary>
    [Fact]
    public void Set_AllocationFails_LeavesWorldUnchanged()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        FailingAllocator allocator = new(tracking);
        using (EcsWorld world = EntityTestSupport.NewWorld(allocator, initialEntityCapacity: 4096))
        {
            RegisterInOrder(world);
            Entity[] sources = [world.Spawn(new Health(0), new Payload(0)), world.Spawn(new Health(1), new Payload(1)), world.Spawn(new Health(2), new Payload(2)), world.Spawn(new Health(3), new Payload(3))];

            // The first Set creates the target archetype and opens its chunk arrays; the despawn empties it again.
            world.Set(sources[0], new Position(0, 0));
            Archetype target = ArchetypeOf(world, sources[0]);
            world.Despawn(sources[0]);
            Assert.Equal(0, target.Chunks.ChunkCount);

            // An empty pool, so the target's new chunk needs the allocator.
            world.TrimMemory();
            world.SetChangeTickForTesting(9);
            string before = Dump(world);
            long liveBlocks = tracking.LiveAllocationCount;
            allocator.FailNext = true;

            // A row that isn't the source's last, so a swap-remove before the failure would show.
            _ = Assert.Throws<InsufficientMemoryException>(() => world.Set(sources[1], new Position(-1, -1)));

            Assert.False(allocator.FailNext);
            Assert.Equal(before, Dump(world));
            AssertDense(world);
            Assert.Equal(liveBlocks, tracking.LiveAllocationCount);
            Assert.False(world.Has<Position>(sources[1]));
            Assert.Equal(1, world.Get<Health>(sources[1]).Value);

            // Still usable: the same Set now succeeds and keeps the other values.
            world.Set(sources[1], new Position(-1, -1));
            Assert.Equal(new Position(-1, -1), world.Get<Position>(sources[1]));
            Assert.Equal(1, world.Get<Health>(sources[1]).Value);
            Assert.True(EntityComponents.Same(new Payload(1), world.Get<Payload>(sources[1])));
            AssertDense(world);
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// Sets a component the entity has: the entity keeps its location, the value is replaced, and every entity's
    /// expected components still match.
    /// </summary>
    private static void SetInPlace<T>(EcsWorld world, Expected expected, Entity entity, in T value, Func<Values, Values> change)
        where T : unmanaged, IComponent
    {
        Assert.True(world.Has<T>(entity));
        (int Archetype, int Chunk, int Row) location = Where(world, entity);
        world.Set(entity, value);
        expected[entity] = change(expected[entity]);
        Assert.Equal(location, Where(world, entity));
        AssertDense(world);
        expected.AssertWorld(world);
    }

    private static void Remove<T>(EcsWorld world, Expected expected, Entity entity)
        where T : unmanaged, IComponent
    {
        Assert.True(world.Remove<T>(entity));
        Values values = expected[entity];
        expected[entity] = typeof(T) == typeof(Position) ? values with { Position = null }
            : typeof(T) == typeof(Health) ? values with { Health = null }
            : typeof(T) == typeof(Payload) ? values with { Payload = null }
            : values with { Marker = false };
        AssertDense(world);
        expected.AssertWorld(world);
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
