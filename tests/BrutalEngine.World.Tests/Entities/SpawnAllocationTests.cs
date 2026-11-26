using System;
using BrutalEngine.Core.Memory;
using Xunit;

namespace BrutalEngine.World.Tests.Entities;

/// <summary>
/// Steady spawn and despawn churn allocates no managed memory and makes no allocator calls (docs/design/world.md,
/// section 10). Measured on this thread, with background collection off in this test host (AllocationMeasurementTests).
/// </summary>
public sealed class SpawnAllocationTests
{
    private const int Rounds = 50;

    [Fact]
    public void Churn_DoesNotAllocate()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // More than a chunk, so the churn opens and returns chunks; a shuffled despawn order, so rows move.
            int count = world.Archetypes.Empty.Layout.Capacity + 100;
            var entities = new Entity[count];
            int[] order = Shuffled(count, seed: 12345);

            // Warm up: grow the entity table and the chunk arrays, fill the chunk pool, and JIT every path.
            Churn(world, entities, order);
            Churn(world, entities, order);

            long nativeAllocations = tracking.TotalAllocationCount;
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < Rounds; round++)
            {
                Churn(world, entities, order);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(nativeAllocations, tracking.TotalAllocationCount);
            Assert.Equal(0, world.EntityCount);
        }

        tracking.ThrowIfLeaks();
    }

    private static void Churn(EcsWorld world, Entity[] entities, int[] order)
    {
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = world.Spawn();
        }

        foreach (int i in order)
        {
            world.Despawn(entities[i]);
        }
    }

    /// <summary>0 to <paramref name="count"/> - 1 in a fixed pseudo-random order.</summary>
    private static int[] Shuffled(int count, int seed)
    {
        int[] order = new int[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
        }

        new Random(seed).Shuffle(order);
        return order;
    }
}
