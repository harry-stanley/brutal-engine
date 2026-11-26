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

    [Fact]
    public void TypedChurnAndReads_DoNotAllocate()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // Spawns of every arity over four archetypes (the two with Payload past a chunk), reads of each kind, and a
            // shuffled despawn.
            const int Count = 400;
            var entities = new Entity[Count];
            int[] order = Shuffled(Count, seed: 54321);

            TypedChurn(world, entities, order);
            TypedChurn(world, entities, order);

            long nativeAllocations = tracking.TotalAllocationCount;
            int archetypes = world.ArchetypeCount;
            long before = GC.GetAllocatedBytesForCurrentThread();
            long checksum = 0;
            for (int round = 0; round < Rounds; round++)
            {
                checksum += TypedChurn(world, entities, order);
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

    private static long TypedChurn(EcsWorld world, Entity[] entities, int[] order)
    {
        long checksum = 0;
        for (int i = 0; i < entities.Length; i++)
        {
            entities[i] = (i % 4) switch
            {
                0 => world.Spawn(new Health(i)),
                1 => world.Spawn(new Position(i, i), new Health(i)),
                2 => world.Spawn(new Payload(i), new Health(i), default(Marker)),
                _ => world.Spawn(default(Marker), new Health(i), new Position(i, i), new Payload(i)),
            };
        }

        foreach (Entity entity in entities)
        {
            checksum += world.Get<Health>(entity).Value;
            checksum += world.Has<Marker>(entity) ? 1 : 0;
            checksum += world.TryGet(entity, out Position position) ? (long)position.X : 0;
            checksum += world.TryGet(entity, out Unused unused) ? unused.Value : 0;
        }

        foreach (int i in order)
        {
            world.Despawn(entities[i]);
        }

        return checksum;
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
