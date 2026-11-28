using System;
using System.Collections.Generic;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// <c>foreach (ChunkView chunk in query)</c> (docs/design/world.md, section 5): every matching chunk once, in archetype
/// creation order then chunk order, with the right entities and columns; and no managed allocation over a million
/// entities. Measured on this thread, with background collection off in this test host (AllocationMeasurementTests).
/// </summary>
public sealed class QueryEnumerationTests
{
    [Fact]
    public void Foreach_YieldsEveryMatchingChunkOnce()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            List<Entity> expected = [];
            _ = world.Spawn(new Health(1));
            expected.AddRange(EnumerationSupport.SpawnChunks(world, chunks: 3, lastRows: 5));
            for (int i = 0; i < 4; i++)
            {
                _ = world.Spawn(new Payload(100 + i), new Marker());
                expected.Add(world.Spawn(new Payload(200 + i)));
            }

            // A row removed from the middle is filled from its archetype's last row, so order isn't spawn order.
            world.Despawn(expected[3]);
            expected.RemoveAt(3);
            using Query query = world.Query().Read<Payload>().OptionalRead<Health>().Without<Marker>().Build();
            List<Entity> seen = [];
            List<(int Archetype, int Chunk)> order = [];
            foreach (ChunkView chunk in query)
            {
                ReadOnlySpan<Entity> rows = chunk.Entities;
                ReadOnlySpan<Payload> payloads = chunk.Read<Payload>();
                Assert.Equal(chunk.Count, rows.Length);
                Assert.Equal(chunk.Count, payloads.Length);
                bool hasHealth = chunk.Has<Health>();
                for (int row = 0; row < rows.Length; row++)
                {
                    Assert.True(EntityComponents.Same(world.Get<Payload>(rows[row]), payloads[row]));
                    Assert.Equal(world.Has<Health>(rows[row]), hasHealth);
                    if (hasHealth)
                    {
                        Assert.Equal(world.Get<Health>(rows[row]).Value, chunk.Read<Health>()[row].Value);
                    }

                    seen.Add(rows[row]);
                }

                EntityRecord record = world.Entities.Validate(rows[0]);
                order.Add((record.Archetype, record.Chunk));
            }

            Assert.Equal(expected.Count, seen.Count);
            Assert.Equal(new HashSet<Entity>(expected), new HashSet<Entity>(seen));

            // (Payload, Health) has 3 chunks, then (Payload) 1: creation order, then chunk order.
            int both = EnumerationSupport.ArchetypeOf(world, expected[0]).Index;
            int payloadOnly = EnumerationSupport.ArchetypeOf(world, expected[^1]).Index;
            Assert.Equal([(both, 0), (both, 1), (both, 2), (payloadOnly, 0)], order);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Million_DoesNotAllocate()
    {
        const int Entities = 1_000_000;
        TrackingAllocator tracking = new(new HeapAllocator(), "Queries", initialCapacity: 4096);
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking, InitialEntityCapacity = Entities }))
        {
            for (int i = 0; i < Entities; i++)
            {
                _ = world.Spawn(new Position(i, 0), new Health(1));
            }

            using Query movers = world.Query().Write<Position>().Read<Health>().OptionalRead<Payload>().Build();
            long checksum = Round(movers, out _);
            int[] rows = new int[3];
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < rows.Length; round++)
            {
                checksum += Round(movers, out rows[round]);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);

            // Each round's loop visits every row once.
            Assert.Equal([Entities, Entities, Entities], rows);

            Assert.True(world.ChunkCount > 100, "the rows fit too few chunks");
            Assert.NotEqual(0, checksum);
        }

        tracking.ThrowIfLeaks();
    }

    // One frame's worth of enumeration, every view member included: a full loop and an early break. Returns a checksum
    // of what it read.
    private static long Round(Query movers, out int rows)
    {
        long checksum = 0;
        rows = 0;
        foreach (ChunkView chunk in movers)
        {
            Span<Position> positions = chunk.Write<Position>();
            ReadOnlySpan<Health> health = chunk.Read<Health>();
            for (int i = 0; i < positions.Length; i++)
            {
                positions[i].Y += health[i].Value;
            }

            rows += chunk.Count;
            checksum += chunk.Entities[0].Index + (chunk.Has<Payload>() ? 1 : 2);
        }

        foreach (ChunkView chunk in movers)
        {
            checksum += chunk.Count;
            break;
        }

        return checksum;
    }
}
