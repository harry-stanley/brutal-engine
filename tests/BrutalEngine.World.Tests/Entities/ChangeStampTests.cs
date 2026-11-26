using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using Xunit;

namespace BrutalEngine.World.Tests.Entities;

/// <summary>
/// The change ticks the direct API stamps in each chunk (docs/design/world.md, sections 4 and 5): spawning stamps
/// <c>Changed</c> and <c>Added</c> for every column of the row's chunk; reads stamp nothing.
/// </summary>
public sealed class ChangeStampTests
{
    [Fact]
    public void Get_DoesNotStamp_Spawn_StampsBoth()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Assert.Equal(1u, world.ChangeTick);
            world.SetChangeTickForTesting(5);
            Entity first = world.Spawn(new Position(1, 2), new Health(3), default(Marker));
            Archetype archetype = ArchetypeOf(world, first);

            // Two columns (the tag has none), both stamped when the chunk opened.
            AssertTicks(archetype, chunk: 0, changed: [5, 5], added: [5, 5]);

            // Reads at a later tick stamp nothing.
            world.SetChangeTickForTesting(9);
            _ = world.Get<Position>(first);
            _ = world.Get<Health>(first);
            _ = world.TryGet(first, out Position _);
            _ = world.Has<Health>(first);
            AssertTicks(archetype, chunk: 0, changed: [5, 5], added: [5, 5]);

            // A spawn into the existing chunk stamps every column of it.
            Entity second = world.Spawn(new Health(4), new Position(5, 6), default(Marker));
            Assert.Equal(archetype.Index, ArchetypeOf(world, second).Index);
            AssertTicks(archetype, chunk: 0, changed: [9, 9], added: [9, 9]);
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
