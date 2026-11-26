using System;
using System.Runtime.CompilerServices;
using System.Threading;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using Xunit;

namespace BrutalEngine.World.Tests.EntityTable;

/// <summary>How a world creates, owns and releases its entity table.</summary>
public sealed class WorldEntityTableTests
{
    [Fact]
    public void Constructor_SizesEntityTableFromOptions()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "World");
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking, InitialEntityCapacity = 48, MaxEntities = 100 }))
        {
            Assert.Equal(48, world.Entities.Capacity);
            Assert.Equal(100, world.Entities.MaxEntities);
            Assert.Equal(0, world.Entities.SlotCount);

            // The table's records and its free-index ring, both from the world's allocator.
            Assert.Equal(2, tracking.LiveAllocationCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Constructor_AllocatorFails_Throws()
    {
        _ = Assert.Throws<InsufficientMemoryException>(() => new EcsWorld(new WorldOptions { Allocator = new ExhaustedAllocator() }));
    }

    [Fact]
    public void Constructor_InvalidOptions_AllocatesNothing()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "World");

        _ = Assert.Throws<ArgumentOutOfRangeException>(
            () => new EcsWorld(new WorldOptions { Allocator = tracking, InitialEntityCapacity = 0 }));

        // Invalid options are rejected before anything is allocated.
        Assert.Equal(0, tracking.TotalAllocationCount);
    }

    [Fact]
    public void IsAlive_IsPending_Lookups()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "World");
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking, InitialEntityCapacity = 4 }))
        {
            Entity alive = world.Entities.Allocate();
            world.Entities.Place(0, archetype: 0, chunk: 0, row: 0);
            Entity pending = world.Entities.Reserve();
            Entity stale = world.Entities.Allocate();
            world.Entities.Place(2, archetype: 0, chunk: 0, row: 1);
            world.Entities.Free(2);

            Assert.True(world.IsAlive(alive));
            Assert.False(world.IsPending(alive));
            Assert.False(world.IsAlive(pending));
            Assert.True(world.IsPending(pending));

            // Lookups never throw for entities that aren't alive here: null, stale, or foreign and out of range.
            Entity[] notAlive = [Entity.Null, stale, new Entity(4096, 1), new Entity(alive.Index, alive.Generation + 1)];
            foreach (Entity entity in notAlive)
            {
                Assert.False(world.IsAlive(entity));
                Assert.False(world.IsPending(entity));
            }
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void IsAlive_IsPending_AfterDispose_Throw()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "World");
        EcsWorld world = new(new WorldOptions { Allocator = tracking });
        world.Dispose();

        _ = Assert.Throws<ObjectDisposedException>(() => world.IsAlive(Entity.Null));
        _ = Assert.Throws<ObjectDisposedException>(() => world.IsPending(Entity.Null));
        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData("EcsWorld.IsAlive")]
    [InlineData("EcsWorld.IsPending")]
    public void IsAlive_IsPending_OtherThread_Throw(string apiName)
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "World");
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            Exception? failure = null;
            Thread thread = new(() =>
            {
                // Caught here, so a failure can't escape the thread and take down the test host.
                failure = Record.Exception(() => _ = apiName == "EcsWorld.IsAlive" ? world.IsAlive(Entity.Null) : world.IsPending(Entity.Null));
            });
            thread.Start();
            thread.Join();

            WrongThreadException exception = Assert.IsType<WrongThreadException>(failure);
            Assert.Equal(apiName, exception.ApiName);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void IsAlive_IsPending_DoNotAllocate()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "World");
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking, InitialEntityCapacity = 4 }))
        {
            Entity alive = world.Entities.Allocate();
            world.Entities.Place(0, archetype: 0, chunk: 0, row: 0);
            Entity pending = world.Entities.Reserve();
            Entity[] probes = [alive, pending, Entity.Null, new Entity(1000, 1)];

            // Warm up so any one-time JIT or tiering work happens before measuring.
            int found = Probe(world, probes);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                found += Probe(world, probes);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.Equal(1001 * 2, found);

            static int Probe(EcsWorld world, Entity[] probes)
            {
                int found = 0;
                foreach (Entity probe in probes)
                {
                    found += world.IsAlive(probe) ? 1 : 0;
                    found += world.IsPending(probe) ? 1 : 0;
                }

                return found;
            }
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>An allocator that has no memory to give.</summary>
    private sealed unsafe class ExhaustedAllocator : INativeAllocator
    {
        public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0) =>
            throw new InsufficientMemoryException("Injected allocation failure.");

        public void Free(void* memory) => throw new InvalidOperationException("Nothing was allocated.");
    }
}
