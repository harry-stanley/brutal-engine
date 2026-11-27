using System;
using System.Runtime.CompilerServices;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// A built query's lifetime (docs/design/world.md, sections 1 and 5): its last-run tick slot, disposal by the query and
/// by the world, the owner-thread and disposal checks, and disposal without managed allocation. Measured on this
/// thread, with background collection off in this test host (AllocationMeasurementTests).
/// </summary>
public sealed class QueryLifetimeTests
{
    // Every member but Dispose.
    private static readonly string[] _members = ["Query.World", "Query.LastRunTick", "Query.HasRun"];

    public static TheoryData<string> Members() => [.. _members];

    [Fact]
    public void Build_RegistersLastRunTick_WhichTheClampRaises()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            world.SetChangeTickForTesting(5);
            using Query old = world.Query().Read<Position>().Build();
            Assert.Equal(5u, old.LastRunTick);
            Assert.False(old.HasRun);

            // Built just before a clamp boundary: newer than the floor, so the clamp leaves it.
            const uint Boundary = 1u << 31;
            world.SetChangeTickForTesting(Boundary - 1);
            using Query recent = world.Query().Build();
            Assert.Equal(Boundary - 1, recent.LastRunTick);
            Assert.Equal(2, world.TickConsumers.Count);
            Assert.NotEqual(old.TickSlot, recent.TickSlot);

            // Crossing 2^31 clamps every consumer older than 2^31 - 2^30 to that value.
            world.AdvanceChangeTick();
            Assert.Equal(Boundary, world.ChangeTick);
            Assert.Equal(1u << 30, old.LastRunTick);
            Assert.Equal(Boundary - 1, recent.LastRunTick);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_FreesCacheAndReleasesTickSlot()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = world.Spawn(new Position(1, 2));
            using Query kept = world.Query().Read<Position>().Build();
            Query query = world.Query().Write<Position>().Build();
            kept.Cache.Refresh();
            query.Cache.Refresh();
            Assert.Equal(1, query.Cache.Count);
            int live = tracking.LiveAllocationCount;
            int slot = query.TickSlot;

            query.Dispose();

            // The cache's array is freed, the slot released and the query unlinked; the other query is untouched.
            Assert.Equal(live - 1, tracking.LiveAllocationCount);
            Assert.Equal(1, world.TickConsumers.Count);
            Assert.Equal(1, world.LiveQueryCount);
            Assert.Equal(0, query.Cache.Count);
            Assert.Equal(1, kept.Cache.Count);
            AssertDisposed(query, typeof(Query));

            // Idempotent, and a new query reuses the released slot.
            query.Dispose();
            Assert.Equal(live - 1, tracking.LiveAllocationCount);
            using Query next = world.Query().Build();
            Assert.Equal(slot, next.TickSlot);
            Assert.Equal(2, world.LiveQueryCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_FreeThrows_QueryStillReleased()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        ThrowAfterFreeAllocator throwing = new(tracking);
        using (EcsWorld world = EntityTestSupport.NewWorld(throwing))
        {
            _ = world.Spawn(new Position(1, 2));
            Query query = world.Query().Read<Position>().Build();
            query.Cache.Refresh();
            Assert.Equal(1, query.Cache.Count);
            throwing.ThrowAfterNextFree = true;
            int frees = throwing.FreeCalls;

            _ = Assert.Throws<InvalidOperationException>(query.Dispose);

            // The cache was freed once, and the query is gone from the world and disposed all the same.
            Assert.Equal(frees + 1, throwing.FreeCalls);
            Assert.Equal(0, world.LiveQueryCount);
            Assert.Equal(0, world.TickConsumers.Count);
            AssertDisposed(query, typeof(Query));

            // A second Dispose does nothing: no free, no throw. World disposal doesn't reach the query either.
            query.Dispose();
            Assert.Equal(frees + 1, throwing.FreeCalls);
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("01")]
    [InlineData("21")]
    [InlineData("12")]
    [InlineData("012")]
    [InlineData("210")]
    [InlineData("102")]
    public void WorldDispose_ReleasesLiveQueries(string disposedFirst)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        _ = world.Spawn(new Position(1, 2), new Health(3));
        Query[] queries =
        [
            world.Query().Read<Position>().Build(),
            world.Query().Read<Health>().Build(),
            world.Query().With<Position>().Build(),
        ];
        foreach (Query query in queries)
        {
            query.Cache.Refresh();
            Assert.Equal(1, query.Cache.Count);
        }

        foreach (char index in disposedFirst)
        {
            queries[index - '0'].Dispose();
        }

        Assert.Equal(queries.Length - disposedFirst.Length, world.LiveQueryCount);
        world.Dispose();

        // Every cache is freed exactly once (the tracking allocator rejects a second free), and every query is unusable.
        tracking.ThrowIfLeaks();
        foreach (Query query in queries)
        {
            AssertDisposed(query, typeof(EcsWorld));
            query.Dispose();
        }

        Assert.Equal(0, world.LiveQueryCount);
    }

    [Fact]
    public void Members_WorldDisposed_ThrowButDisposeDoesNothing()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        Query query = world.Query().Read<Position>().Build();
        world.Dispose();

        AssertDisposed(query, typeof(EcsWorld));
        query.Dispose();
        query.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Theory]
    [MemberData(nameof(Members))]
    [InlineData("Query.Dispose")]
    public void Member_OtherThread_Throws(string apiName)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = world.Spawn(new Position(1, 2));
            using Query query = world.Query().Read<Position>().Build();

            Exception? failure = EntityTestSupport.RunOnOtherThread(() => Call(query, apiName));

            WrongThreadException exception = Assert.IsType<WrongThreadException>(failure);
            Assert.Equal(apiName, exception.ApiName);

            // Nothing changed: the cache wasn't refreshed and the query is still live.
            Assert.Equal(0, query.Cache.ScannedArchetypes);
            Assert.Equal(1, world.LiveQueryCount);
            query.Cache.Refresh();
            Assert.Equal(1, query.Cache.Count);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void World_IsTheBuildingWorld()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        using (EcsWorld other = EntityTestSupport.NewWorld(tracking))
        {
            using Query query = world.Query().Build();
            using Query otherQuery = other.Query().Build();
            Assert.Same(world, query.World);
            Assert.Same(other, otherQuery.World);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_DoesNotAllocate()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();

        // Warm up both disposal paths on a first world: a query's own, and the world's release of live queries.
        RunDisposals(tracking, out _, out _);

        RunDisposals(tracking, out long queryDispose, out long worldDispose);

        Assert.Equal(0, queryDispose);
        Assert.Equal(0, worldDispose);
        tracking.ThrowIfLeaks();
    }

    private static void RunDisposals(TrackingAllocator tracking, out long queryDispose, out long worldDispose)
    {
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        _ = world.Spawn(new Position(1, 2), new Health(3));
        Query disposed = world.Query().Read<Position>().Build();
        Query live = world.Query().Read<Health>().Build();
        Query alsoLive = world.Query().Build();
        disposed.Cache.Refresh();
        live.Cache.Refresh();
        alsoLive.Cache.Refresh();
        int sum = disposed.Cache.Count + live.Cache.Count + alsoLive.Cache.Count;
        int liveBlocks = tracking.LiveAllocationCount;

        long before = GC.GetAllocatedBytesForCurrentThread();
        disposed.Dispose();
        queryDispose = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(liveBlocks - 1, tracking.LiveAllocationCount);

        before = GC.GetAllocatedBytesForCurrentThread();
        world.Dispose();
        worldDispose = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, tracking.LiveAllocationCount);
        Assert.Equal(1 + 1 + 2, sum);
    }

    private static object Call(Query query, string apiName)
    {
        switch (apiName)
        {
            case "Query.World":
                return query.World;
            case "Query.LastRunTick":
                return query.LastRunTick;
            case "Query.HasRun":
                return query.HasRun;
            case "Query.Dispose":
                query.Dispose();
                return query;
            default:
                throw new ArgumentOutOfRangeException(nameof(apiName), apiName, "Unknown member.");
        }
    }

    // Every member but Dispose throws ObjectDisposedException naming the disposed object's type.
    private static void AssertDisposed(Query query, Type disposed)
    {
        foreach (string apiName in _members)
        {
            ObjectDisposedException exception = Assert.Throws<ObjectDisposedException>(() => Call(query, apiName));
            Assert.Equal(disposed.FullName, exception.ObjectName);
        }
    }

    /// <summary>Passes calls through and counts frees; when asked, throws after one free has freed.</summary>
    private sealed unsafe class ThrowAfterFreeAllocator(INativeAllocator inner) : INativeAllocator
    {
        /// <summary>Set to throw after the next free; cleared when it throws.</summary>
        public bool ThrowAfterNextFree { get; set; }

        public int FreeCalls { get; private set; }

        public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0) =>
            inner.Allocate(byteCount, alignment, callerFile, callerLine);

        public void Free(void* memory)
        {
            FreeCalls++;
            inner.Free(memory);
            if (ThrowAfterNextFree)
            {
                ThrowAfterNextFree = false;
                throw new InvalidOperationException("Injected free failure.");
            }
        }
    }

}
