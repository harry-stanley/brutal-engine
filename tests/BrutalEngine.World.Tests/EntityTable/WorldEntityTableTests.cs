using System;
using System.Runtime.CompilerServices;
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

    /// <summary>An allocator that has no memory to give.</summary>
    private sealed unsafe class ExhaustedAllocator : INativeAllocator
    {
        public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0) =>
            throw new InsufficientMemoryException("Injected allocation failure.");

        public void Free(void* memory) => throw new InvalidOperationException("Nothing was allocated.");
    }
}
