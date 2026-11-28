using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// The <c>Changed</c> and <c>Added</c> chunk filters (docs/design/world.md, section 5, "Change detection"): a chunk is
/// yielded when every filter's tick is newer than the query's last run, the first run matches every chunk by a flag, and
/// the comparison survives wrap-around and the clamp. One row per rule.
/// </summary>
public sealed class ChangeFilterTests
{
    [Fact]
    public void Changed_Added_AllMustHold_FirstRunMatchesAll()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity[] entities = EnumerationSupport.SpawnChunks(world, chunks: 3, lastRows: 4);
            Archetype archetype = EnumerationSupport.ArchetypeOf(world, entities[0]);
            int capacity = archetype.Layout.Capacity;

            // Built well after the stamps, so a baseline tick would reject them all: the first run is a flag.
            for (int i = 0; i < 5; i++)
            {
                world.AdvanceChangeTick();
            }

            using Query changed = world.Query().Read<Payload>().Changed<Payload>().Build();
            using Query added = world.Query().Added<Health>().Build();
            using Query both = world.Query().Changed<Payload>().Added<Health>().Build();
            using Query twoChanged = world.Query().Changed<Payload>().Changed<Health>().Build();
            foreach (Query query in new[] { changed, added, both, twoChanged })
            {
                Assert.False(query.HasRun);
                Assert.Equal(6u, query.LastRunTick);
                Assert.Equal([0, 1, 2], EnumerationSupport.YieldedChunks(world, query));
                Assert.True(query.HasRun);
                Assert.Empty(EnumerationSupport.YieldedChunks(world, query));
            }

            // Chunk 1: Payload changed only. Chunk 0: Health changed only. Chunk 2: a spawn stamps Changed and Added.
            world.Write<Payload>(entities[capacity + 1]).Values[0] = 9;
            world.Write<Health>(entities[2]).Value = 9;
            _ = world.Spawn(new Payload(1), new Health(1));
            Assert.Equal([1, 2], EnumerationSupport.YieldedChunks(world, changed));
            Assert.Equal([2], EnumerationSupport.YieldedChunks(world, added));
            Assert.Equal([2], EnumerationSupport.YieldedChunks(world, both));
            Assert.Equal([2], EnumerationSupport.YieldedChunks(world, twoChanged));

            // Added newer but Changed not (no API does that alone, so through the tick table): Changed must hold too.
            world.AdvanceChangeTick();
            archetype.Chunks.GetAdded(1)[EnumerationSupport.ColumnOf<Health>(world, archetype)] = world.ChangeTick;
            Assert.Equal([1], EnumerationSupport.YieldedChunks(world, added));
            Assert.Empty(EnumerationSupport.YieldedChunks(world, both));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void ViewChangedAndAdded_FollowTheLastRun()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity[] entities = EnumerationSupport.SpawnChunks(world, chunks: 2, lastRows: 1);
            using Query query = world.Query().Read<Payload>().Read<Health>().Build();
            world.SetChangeTickForTesting(10);
            int firstRun = 0;
            foreach (ChunkView chunk in query)
            {
                // Before the first completed run everything that exists is new, whatever the ticks.
                Assert.True(chunk.Changed<Payload>());
                Assert.True(chunk.Added<Health>());
                firstRun++;
            }

            Assert.Equal(2, firstRun);
            world.Write<Payload>(entities[^1]).Values[0] = 1;
            int seen = 0;
            foreach (ChunkView chunk in query)
            {
                // Only the last chunk (index 1) was written; nothing was added since the last run.
                bool last = EnumerationSupport.ChunkOf(world, chunk.Entities[0]) == 1;
                Assert.Equal(last, chunk.Changed<Payload>());
                Assert.False(chunk.Changed<Health>());
                Assert.False(chunk.Added<Payload>());
                seen++;
            }

            Assert.Equal(2, seen);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Filters_AcrossWrapAround()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            world.SetChangeTickForTesting(uint.MaxValue - 1);
            Entity entity = world.Spawn(new Payload(1), new Health(1));
            using Query query = world.Query().Changed<Payload>().Read<Health>().Build();
            Assert.Single(EnumerationSupport.YieldedChunks(world, query));
            Assert.Equal(uint.MaxValue - 1, query.LastRunTick);

            // Stamped at uint.MaxValue, then at 0 after the wrap: each is newer than the run before it.
            world.Write<Payload>(entity).Values[0] = 2;
            Assert.Single(EnumerationSupport.YieldedChunks(world, query));
            Assert.Equal(uint.MaxValue, query.LastRunTick);
            Assert.Equal(0u, world.ChangeTick);

            // Stamped at 0 against a last run of uint.MaxValue: the view's Changed and Added see it too.
            world.Write<Payload>(entity).Values[0] = 3;
            _ = world.Spawn(new Payload(4), new Health(4));
            int seen = 0;
            foreach (ChunkView chunk in query)
            {
                Assert.True(chunk.Changed<Payload>());
                Assert.True(chunk.Added<Health>());
                seen++;
            }

            Assert.Equal(1, seen);
            Assert.Equal(0u, query.LastRunTick);
            Assert.Empty(EnumerationSupport.YieldedChunks(world, query));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Filters_IdleAcrossClamp_StillSeePendingChange()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity[] entities = EnumerationSupport.SpawnChunks(world, chunks: 2, lastRows: 1);
            using Query query = world.Query().Changed<Payload>().Build();
            Assert.Equal(2, EnumerationSupport.YieldedChunks(world, query).Length);
            world.Write<Payload>(entities[^1]).Values[0] = 1;

            // The query idles until the tick reaches the 2^31 clamp: its last run and the pending stamp are both
            // clamped, the query one below the stamps, so the write is still newer.
            world.SetChangeTickForTesting((1u << 31) - 1);
            world.AdvanceChangeTick();
            Assert.Equal(1u << 30, query.LastRunTick);
            Assert.Contains(1, EnumerationSupport.YieldedChunks(world, query));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void TagFilter_HoldsOnlyOnTheFirstRun()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = world.Spawn(new Position(1, 1), new Marker());
            using Query query = world.Query().Read<Position>().Changed<Marker>().Build();
            foreach (ChunkView chunk in query)
            {
                Assert.True(chunk.Changed<Marker>());
                Assert.True(chunk.Added<Marker>());
            }

            // A tag has no ticks: nothing can stamp it, so after the first run its filter never holds.
            _ = world.Spawn(new Position(2, 2), new Marker());
            Assert.Empty(EnumerationSupport.YieldedChunks(world, query));
            using Query plain = world.Query().Read<Position>().Read<Marker>().Build();
            Assert.Single(EnumerationSupport.YieldedChunks(world, plain));
            foreach (ChunkView chunk in plain)
            {
                Assert.False(chunk.Changed<Marker>());
            }
        }

        tracking.ThrowIfLeaks();
    }
}
