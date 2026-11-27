using System;
using System.Globalization;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Internal;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// <see cref="Query.CountEntities"/> (docs/design/world.md, section 5): matching rows with the change filters ignored,
/// rows and archetypes that change after the build, and no managed allocation. Measured on this thread, with background
/// collection off in this test host (AllocationMeasurementTests).
/// </summary>
public sealed class QueryTests
{
    [Fact]
    public void CountEntities_IgnoresFilters()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Spawn(world, 3, () => world.Spawn(new Position(1, 2)));
            Spawn(world, 2, () => world.Spawn(new Position(3, 4), new Health(5)));
            Spawn(world, 4, () => world.Spawn(new Position(6, 7), new Marker()));
            Spawn(world, 1, () => world.Spawn(new Health(8)));
            using Query filtered = world.Query().Read<Position>().Changed<Position>().Added<Health>().Without<Marker>().Build();
            using Query unfiltered = world.Query().Read<Position>().With<Health>().Without<Marker>().Build();
            using Query changedOnly = world.Query().Changed<Position>().Build();

            // Every stamp is older than each query's last-run tick, so a chunk filter would yield nothing.
            world.AdvanceChangeTick();
            world.AdvanceChangeTick();
            foreach (Query query in new[] { filtered, unfiltered, changedOnly })
            {
                world.TickConsumers.Set(query.TickSlot, world.ChangeTick);
                Assert.Equal(world.ChangeTick, query.LastRunTick);
            }

            // The filters count only as required components: Changed<Position> requires Position, Added<Health> Health.
            Assert.Equal(2, filtered.CountEntities());
            Assert.Equal(2, unfiltered.CountEntities());
            Assert.Equal(9, changedOnly.CountEntities());
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void CountEntities_FollowsRowsAndNewArchetypes()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            using Query query = world.Query().Read<Payload>().Without<Marker>().Build();
            Assert.Equal(0, query.CountEntities());

            // A 256-byte row fills a 16 KiB chunk with a few dozen rows, so 100 rows take several chunks.
            var entities = new Entity[100];
            for (int i = 0; i < entities.Length; i++)
            {
                entities[i] = world.Spawn(new Payload(i));
            }

            Assert.True(world.ChunkCount >= 2, "the rows fit one chunk");
            Assert.Equal(100, query.CountEntities());

            // Archetypes created after the build are counted, excluded ones aren't, and moves and despawns are seen.
            Spawn(world, 3, () => world.Spawn(new Payload(7), new Health(1)));
            Spawn(world, 5, () => world.Spawn(new Payload(8), new Marker()));
            world.Add(entities[0], new Health(2));
            world.Add(entities[1], new Marker());
            for (int i = 2; i < 12; i++)
            {
                world.Despawn(entities[i]);
            }

            Assert.Equal(100 + 3 - 1 - 10, query.CountEntities());
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void CountEntities_DoesNotAllocate()
    {
        const int Rounds = 12;
        const int PerRound = 3;
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = NewWorldWithSpareIds(tracking, out ComponentId[] spare))
        {
            for (int i = 0; i < 20; i++)
            {
                _ = world.Spawn(new Position(i, i));
                _ = world.Spawn(new Position(i, i), new Health(i));
                _ = world.Spawn(new Position(i, i), new Marker());
            }

            using Query query = world.Query().Read<Position>().Changed<Position>().OptionalRead<Health>().Without<Marker>().Build();
            ComponentId position = world.Components.Register<Position>();

            // Warm up every path, a growth that copies entries included: 10 more matching archetypes take the cache
            // past its first 8 entries.
            for (int i = 0; i < 10; i++)
            {
                _ = world.Archetypes.GetOrCreate([position, spare[spare.Length - 1 - i]]);
            }

            long checksum = Read(query);

            // The archetypes for each round, made up front: creating them allocates (setup work), so it stays outside
            // the measured windows. Each matches: Position and one spare component.
            var requests = new ComponentId[Rounds * PerRound][];
            for (int i = 0; i < requests.Length; i++)
            {
                requests[i] = [position, spare[i]];
            }

            long allocated = 0;
            long nativeBefore = tracking.TotalAllocationCount;
            for (int round = 0; round < Rounds; round++)
            {
                for (int i = 0; i < PerRound; i++)
                {
                    _ = world.Archetypes.GetOrCreate(requests[(round * PerRound) + i]);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();

                // One count that refreshes (and in some rounds grows the cache), then counts with nothing new.
                for (int repeat = 0; repeat < 50; repeat++)
                {
                    checksum += Read(query);
                }

                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Assert.Equal(0, allocated);

            // The windows did append and grow: 12 entries before them and 36 in them, so the array went from 16 to 32
            // and 64 there.
            Assert.Equal(12 + (Rounds * PerRound), query.Cache.Count);
            Assert.True(tracking.TotalAllocationCount - nativeBefore >= 2, "the measured counts never grew the cache");
            Assert.Equal(40, query.CountEntities());
            Assert.NotEqual(0, checksum);
        }

        tracking.ThrowIfLeaks();
    }

    // Every query member that reads, folded into a checksum: what a frame calls.
    private static long Read(Query query) =>
        query.CountEntities() + query.LastRunTick + (query.HasRun ? 1 : 2) + query.World.ArchetypeCount;

    private static void Spawn(EcsWorld world, int count, Func<Entity> spawn)
    {
        for (int i = 0; i < count; i++)
        {
            Assert.True(world.IsAlive(spawn()));
        }
    }

    // A world of 128 component ids: Position, Health and Marker, then made-up 4-byte data components, for archetypes the
    // test creates directly.
    private static EcsWorld NewWorldWithSpareIds(INativeAllocator allocator, out ComponentId[] spare)
    {
        EcsWorld world = new(new WorldOptions
        {
            Allocator = allocator,
            ChunkBytes = WorldOptions.SmallestChunkBytes,
            MaxComponentTypes = QueryTestSupport.MaskBits,
        });
        _ = world.Components.Register<Position>();
        _ = world.Components.Register<Health>();
        _ = world.Components.Register<Marker>();
        spare = new ComponentId[QueryTestSupport.MaskBits - 3];
        for (int i = 0; i < spare.Length; i++)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"Tests.Query.Spare{i}");
            ComponentInfo info = EntityComponents.InfoFor(name);
            spare[i] = world.Components.Add(null, ComponentOrdinal.Reserve(), in info, 4, 4);
        }

        return world;
    }
}
