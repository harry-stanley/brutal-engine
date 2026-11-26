using System;
using System.Threading;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using Xunit;

namespace BrutalEngine.World.Tests.Chunks;

/// <summary>How a world owns its chunk pool, trims it and frees it.</summary>
public sealed unsafe class WorldChunkPoolTests
{
    [Fact]
    public void Constructor_PoolUsesChunkBytesAndAllocatesNothing()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking, ChunkBytes = 32 * 1024 }))
        {
            Assert.Equal(32 * 1024, world.ChunkPool.ChunkBytes);
            Assert.Equal(0, world.ChunkPool.PooledCount);

            // Only the entity table's records and free-index ring: the pool allocates when first rented from.
            Assert.Equal(2, tracking.LiveAllocationCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void TrimMemory_ReleasesPooledBlocks()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            int baseline = tracking.LiveAllocationCount;
            byte* kept = world.ChunkPool.Rent();
            byte* pooled = world.ChunkPool.Rent();
            world.ChunkPool.Return(pooled);
            Assert.Equal(baseline + 2, tracking.LiveAllocationCount);

            world.TrimMemory();

            // The pooled block went back to the allocator; the rented one didn't.
            Assert.Equal(baseline + 1, tracking.LiveAllocationCount);
            Assert.Equal(0, world.ChunkPool.PooledCount);
            world.ChunkPool.Return(kept);

            // Trimming an emptied pool again is harmless.
            world.TrimMemory();
            world.TrimMemory();
            Assert.Equal(baseline, tracking.LiveAllocationCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_FreesPooledBlocks()
    {
        TrackingAllocator tracking = NewTracking();
        EcsWorld world = new(new WorldOptions { Allocator = tracking });
        byte* a = world.ChunkPool.Rent();
        byte* b = world.ChunkPool.Rent();
        world.ChunkPool.Return(a);
        world.ChunkPool.Return(b);

        world.Dispose();

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void TrimMemory_AfterDispose_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        EcsWorld world = new(new WorldOptions { Allocator = tracking });
        world.Dispose();

        ObjectDisposedException exception = Assert.Throws<ObjectDisposedException>(world.TrimMemory);

        Assert.Equal(typeof(EcsWorld).FullName, exception.ObjectName);
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void TrimMemory_OtherThread_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            world.ChunkPool.Return(world.ChunkPool.Rent());
            int baseline = tracking.LiveAllocationCount;
            Exception? failure = null;
            Thread thread = new(() =>
            {
                try
                {
                    world.TrimMemory();
                }
                catch (Exception ex)
                {
                    // Caught here, so it can't escape the thread and take down the test host.
                    failure = ex;
                }
            });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The other thread didn't finish.");

            WrongThreadException exception = Assert.IsType<WrongThreadException>(failure);
            Assert.Equal("EcsWorld.TrimMemory", exception.ApiName);

            // The rejected call freed nothing.
            Assert.Equal(baseline, tracking.LiveAllocationCount);
            Assert.Equal(1, world.ChunkPool.PooledCount);
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>A fresh tracking allocator per test, so each test's leak check sees only its own blocks.</summary>
    private static TrackingAllocator NewTracking() => new(new HeapAllocator(), "World");
}
