using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using Xunit;

namespace BrutalEngine.World.Tests.Entities;

/// <summary>
/// The change ticks the direct API stamps in each chunk (docs/design/world.md, sections 4 and 5): spawning stamps
/// <c>Changed</c> and <c>Added</c> for every column of the row's chunk, <c>Write</c> stamps its component's
/// <c>Changed</c> tick, a despawn stamps <c>Changed</c> in the chunk that received the moved row, and reads stamp nothing.
/// </summary>
public sealed class ChangeStampTests
{
    [Fact]
    public void GetNoStamp_WriteStamps_SpawnStampsBoth()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // On a world whose tick was never set, a spawn stamps tick 1. Two columns: the tag has none.
            Entity zero = world.Spawn(new Position(0, 0), new Health(0), default(Marker));
            Archetype archetype = ArchetypeOf(world, zero);
            AssertTicks(archetype, chunk: 0, changed: [1, 1], added: [1, 1]);
            Assert.Equal(1u, world.ChangeTick);

            world.SetChangeTickForTesting(5);
            Entity first = world.Spawn(new Position(1, 2), new Health(3), default(Marker));
            Assert.Equal(archetype.Index, ArchetypeOf(world, first).Index);
            AssertTicks(archetype, chunk: 0, changed: [5, 5], added: [5, 5]);

            // Reads at a later tick stamp nothing.
            world.SetChangeTickForTesting(9);
            _ = world.Get<Position>(first);
            _ = world.Get<Health>(first);
            _ = world.TryGet(first, out Position _);
            _ = world.Has<Health>(first);
            AssertTicks(archetype, chunk: 0, changed: [5, 5], added: [5, 5]);

            // Write stamps only its own column's Changed tick (columns follow registration order: Position, Health),
            // and the reference it returns writes the row.
            world.Write<Health>(first).Value = 30;
            AssertTicks(archetype, chunk: 0, changed: [5, 9], added: [5, 5]);
            Assert.Equal(30, world.Get<Health>(first).Value);

            // A write that throws stamps nothing.
            world.SetChangeTickForTesting(10);
            _ = Assert.Throws<InvalidOperationException>(() => world.Write<Payload>(first));
            AssertTicks(archetype, chunk: 0, changed: [5, 9], added: [5, 5]);

            // A spawn into the existing chunk stamps every column of it.
            world.SetChangeTickForTesting(12);
            Entity second = world.Spawn(new Health(4), new Position(5, 6), default(Marker));
            Assert.Equal(archetype.Index, ArchetypeOf(world, second).Index);
            AssertTicks(archetype, chunk: 0, changed: [12, 12], added: [12, 12]);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Spawn_StampsOnlyItsOwnChunk()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            world.SetChangeTickForTesting(3);
            Entity first = world.Spawn(new Payload(1));
            Archetype archetype = ArchetypeOf(world, first);
            for (int i = 1; i < archetype.Layout.Capacity; i++)
            {
                _ = world.Spawn(new Payload(i));
            }

            // The first chunk is full, so the next spawn opens a second one; the first keeps its stamps.
            world.SetChangeTickForTesting(8);
            _ = world.Spawn(new Payload(-1));
            Assert.Equal(2, archetype.Chunks.ChunkCount);
            AssertTicks(archetype, chunk: 0, changed: [3], added: [3]);
            AssertTicks(archetype, chunk: 1, changed: [8], added: [8]);

            // And another spawn into the second chunk at a later tick stamps only that chunk.
            world.SetChangeTickForTesting(11);
            _ = world.Spawn(new Payload(-2));
            AssertTicks(archetype, chunk: 0, changed: [3], added: [3]);
            AssertTicks(archetype, chunk: 1, changed: [11], added: [11]);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Despawn_StampsReceivingChunk()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // A full first chunk and two rows in a second, all at tick 2.
            world.SetChangeTickForTesting(2);
            Entity first = world.Spawn(new Payload(0), new Health(0));
            Archetype archetype = ArchetypeOf(world, first);
            var entities = new Entity[archetype.Layout.Capacity + 2];
            entities[0] = first;
            for (int i = 1; i < entities.Length; i++)
            {
                entities[i] = world.Spawn(new Payload(i), new Health(i));
            }

            // The second chunk's last row moves into the first chunk: only the first chunk's Changed ticks move.
            world.SetChangeTickForTesting(7);
            world.Despawn(entities[3]);
            AssertTicks(archetype, chunk: 0, changed: [7, 7], added: [2, 2]);
            AssertTicks(archetype, chunk: 1, changed: [2, 2], added: [2, 2]);

            // Despawning the last row moves nothing, so nothing is stamped (its emptied chunk goes back to the pool).
            world.SetChangeTickForTesting(8);
            world.Despawn(entities[^2]);
            Assert.Equal(1, archetype.Chunks.ChunkCount);
            AssertTicks(archetype, chunk: 0, changed: [7, 7], added: [2, 2]);

            // A move within the last chunk stamps that chunk only.
            world.SetChangeTickForTesting(9);
            Entity b = world.Spawn(new Payload(-1), new Health(-1));
            Entity c = world.Spawn(new Payload(-2), new Health(-2));
            world.SetChangeTickForTesting(11);
            world.Despawn(b);
            AssertTicks(archetype, chunk: 0, changed: [7, 7], added: [2, 2]);
            AssertTicks(archetype, chunk: 1, changed: [11, 11], added: [9, 9]);

            // Write stamps the written entity's own chunk, here the second (columns: Payload, then Health).
            Assert.True(world.Entities.TryGetLocation(c, out EntityRecord location));
            Assert.Equal(1, location.Chunk);
            world.SetChangeTickForTesting(13);
            world.Write<Health>(c).Value = 99;
            AssertTicks(archetype, chunk: 0, changed: [7, 7], added: [2, 2]);
            AssertTicks(archetype, chunk: 1, changed: [11, 13], added: [9, 9]);

            // Despawning the last row of a chunk that keeps rows moves nothing, so that chunk isn't stamped either.
            world.SetChangeTickForTesting(14);
            Entity d = world.Spawn(new Payload(-3), new Health(-3));
            AssertTicks(archetype, chunk: 1, changed: [14, 14], added: [14, 14]);
            world.SetChangeTickForTesting(15);
            world.Despawn(d);
            Assert.Equal(1, archetype.Chunks.GetCount(1));
            AssertTicks(archetype, chunk: 0, changed: [7, 7], added: [2, 2]);
            AssertTicks(archetype, chunk: 1, changed: [14, 14], added: [14, 14]);
        }

        tracking.ThrowIfLeaks();
    }

    private static Archetype ArchetypeOf(EcsWorld world, Entity entity)
    {
        Assert.True(world.Entities.TryGetLocation(entity, out EntityRecord record));
        return world.Archetypes[record.Archetype];
    }

    private static void AssertTicks(Archetype archetype, int chunk, uint[] changed, uint[] added)
    {
        Assert.Equal(changed, archetype.Chunks.GetChanged(chunk).ToArray());
        Assert.Equal(added, archetype.Chunks.GetAdded(chunk).ToArray());
    }
}
