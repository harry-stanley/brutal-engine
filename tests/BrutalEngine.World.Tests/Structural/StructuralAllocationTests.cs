using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Structural;

/// <summary>
/// Moves between existing archetypes allocate no managed memory and, once the chunk pool holds the churn's chunks,
/// make no allocator calls (docs/design/world.md, section 10). Measured on this thread, with background collection off
/// in this test host (AllocationMeasurementTests).
/// </summary>
public sealed class StructuralAllocationTests
{
    private const int Rounds = 50;

    [Fact]
    public void AddMoves_DoNotAllocate()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // More than a chunk in the archetypes with Payload, so moves open and return chunks there.
            const int Count = 150;
            var entities = new Entity[Count];

            // Warm up: create the archetypes, cache the edges, grow the tables, fill the pool and JIT every path.
            AddChurn(world, entities);
            AddChurn(world, entities);

            long nativeAllocations = tracking.TotalAllocationCount;
            int archetypes = world.ArchetypeCount;
            long checksum = 0;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < Rounds; round++)
            {
                checksum += AddChurn(world, entities);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(nativeAllocations, tracking.TotalAllocationCount);
            Assert.Equal(archetypes, world.ArchetypeCount);
            Assert.Equal(0, world.EntityCount);
            Assert.NotEqual(0, checksum);
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// Spawns entities in two archetypes, adds a data component to each (from the empty archetype and from one with
    /// columns), then a tag, in an order that moves rows out of the middle of chunks, and despawns them all.
    /// </summary>
    private static long AddChurn(EcsWorld world, Entity[] entities)
    {
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = (i % 2) == 0 ? world.Spawn() : world.Spawn(new Health(i), new Payload(i));
        }

        for (int i = 0; i < entities.Length; i++)
        {
            world.Add(entities[i], new Position(i, -i));
        }

        for (int i = entities.Length - 1; i >= 0; i -= 3)
        {
            world.Add(entities[i], default(Marker));
        }

        long checksum = 0;
        foreach (Entity entity in entities)
        {
            checksum += (long)world.Get<Position>(entity).X;
            checksum += world.TryGet(entity, out Health health) ? health.Value : 0;
            world.Despawn(entity);
        }

        return checksum;
    }
}
