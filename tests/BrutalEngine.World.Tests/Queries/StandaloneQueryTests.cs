using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// Standalone query ticks (docs/design/world.md, section 5, "Standalone queries"): a completed enumeration sets the
/// last-run tick to the current change tick and then advances it, so the loop's own writes are unseen and later writes
/// are seen; an enumeration that ends early changes nothing.
/// </summary>
public sealed class StandaloneQueryTests
{
    [Fact]
    public void OwnWritesUnseen_LaterWritesSeen_EarlyBreakUnchanged()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity[] entities = EnumerationSupport.SpawnChunks(world, chunks: 3, lastRows: 2);
            int capacity = EnumerationSupport.ArchetypeOf(world, entities[0]).Layout.Capacity;
            world.SetChangeTickForTesting(20);
            using Query query = world.Query().Write<Payload>().Changed<Payload>().Build();

            // The first run writes every chunk at tick 20, then completes: last run 20, the world at 21.
            Assert.Equal(3, WriteAll(query));
            Assert.Equal(20u, query.LastRunTick);
            Assert.Equal(21u, world.ChangeTick);

            // Its own writes aren't newer than its last run.
            Assert.Equal(0, WriteAll(query));
            Assert.Equal(21u, query.LastRunTick);
            Assert.Equal(22u, world.ChangeTick);

            // A write right after a run, with no other advance, is seen by the next run, and only by that one.
            world.Write<Payload>(entities[capacity]).Values[0] = 5;
            Assert.Equal([1], EnumerationSupport.YieldedChunks(world, query));
            Assert.Empty(EnumerationSupport.YieldedChunks(world, query));

            // An early break leaves the last run, the flag and the world's tick alone, and sees the same chunks again.
            world.Write<Payload>(entities[1]).Values[0] = 6;
            world.Write<Payload>(entities[^1]).Values[0] = 7;
            uint lastRun = query.LastRunTick;
            uint tick = world.ChangeTick;
            foreach (ChunkView chunk in query)
            {
                Assert.Equal(entities[0], chunk.Entities[0]);
                break;
            }

            Assert.Equal(lastRun, query.LastRunTick);
            Assert.Equal(tick, world.ChangeTick);
            Assert.True(query.HasRun);
            Assert.Equal([0, 2], EnumerationSupport.YieldedChunks(world, query));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void FirstRunEndedEarly_StaysFirstRun()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = EnumerationSupport.SpawnChunks(world, chunks: 2, lastRows: 1);
            world.AdvanceChangeTick();
            using Query query = world.Query().Changed<Payload>().Build();
            foreach (ChunkView chunk in query)
            {
                break;
            }

            // An exception out of the loop body is an early end too.
            Assert.Throws<InvalidOperationException>(() =>
            {
                foreach (ChunkView chunk in query)
                {
                    throw new InvalidOperationException("Body failure.");
                }
            });
            Assert.False(query.HasRun);
            Assert.Equal(2u, query.LastRunTick);
            Assert.Equal(2u, world.ChangeTick);
            Assert.Equal([0, 1], EnumerationSupport.YieldedChunks(world, query));
            Assert.True(query.HasRun);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void EmptyQuery_CompletesAndAdvances()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            using Query query = world.Query().Read<Payload>().Build();
            Assert.Empty(EnumerationSupport.YieldedChunks(world, query));
            Assert.True(query.HasRun);
            Assert.Equal(1u, query.LastRunTick);
            Assert.Equal(2u, world.ChangeTick);
        }

        tracking.ThrowIfLeaks();
    }

    // Enumerates to completion, writing every yielded chunk's Payload column; returns the chunks yielded.
    private static int WriteAll(Query query)
    {
        int chunks = 0;
        foreach (ChunkView chunk in query)
        {
            Span<Payload> values = chunk.Write<Payload>();
            values[0].Values[1]++;
            chunks++;
        }

        return chunks;
    }
}
