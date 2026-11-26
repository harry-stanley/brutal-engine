using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using CsCheck;
using Xunit;

namespace BrutalEngine.World.Tests.Archetypes;

public sealed unsafe class ArchetypeEdgesTests
{
    [Fact]
    public void HitAndMiss()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            _ = ArchetypeTestTypes.Forge(world, 3);
            ArchetypeSet set = world.Archetypes;
            ComponentId c0 = new(0);
            int lookups = set.SignatureLookups;

            // A miss asks the set once and creates the neighbour; the same call again reads the cache only.
            Archetype withC0 = set.Empty.WithAdded(c0);
            Assert.Equal(lookups + 1, set.SignatureLookups);
            Assert.Equal(2, set.Count);
            Assert.Same(withC0, set.Empty.WithAdded(c0));
            Assert.Equal(lookups + 1, set.SignatureLookups);

            // The way back is a miss on the other archetype that finds the existing empty archetype, then a hit.
            Assert.Same(set.Empty, withC0.WithRemoved(c0));
            Assert.Same(set.Empty, withC0.WithRemoved(c0));
            Assert.Equal(lookups + 2, set.SignatureLookups);
            Assert.Equal(2, set.Count);

            // An archetype created directly is found, not created again, by the first edge that leads to it.
            Archetype both = set.GetOrCreate(ArchetypeTestTypes.Ids(0, 1));
            int count = set.Count;
            Assert.Same(both, withC0.WithAdded(new ComponentId(1)));
            Assert.Equal(count, set.Count);

            // Many archetypes later (the set's tables grow), the cached edges still lead to the same archetypes.
            for (int i = 0; i < 3 * ArchetypeSet.InitialCapacity; i++)
            {
                _ = set.GetOrCreate(ArchetypeTestTypes.Ids(i % 3, (i / 3) % 3, 2));
                _ = ArchetypeTestTypes.Forge(world, 1);
                _ = set.GetOrCreate(ArchetypeTestTypes.Ids(world.Components.Count - 1));
            }

            lookups = set.SignatureLookups;
            Assert.Same(withC0, set.Empty.WithAdded(c0));
            Assert.Same(set.Empty, withC0.WithRemoved(c0));
            Assert.Same(both, withC0.WithAdded(new ComponentId(1)));
            Assert.Equal(lookups, set.SignatureLookups);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Cached_DoesNotAllocate()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            _ = ArchetypeTestTypes.Forge(world, 2);
            Archetype empty = world.Archetypes.Empty;
            ComponentId c1 = new(1);
            Archetype one = empty.WithAdded(c1);
            _ = one.WithRemoved(c1);
            long nativeAllocations = tracking.TotalAllocationCount;

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                _ = empty.WithAdded(c1).WithRemoved(c1);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(nativeAllocations, tracking.TotalAllocationCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Edges_ManyComponents_AllFound()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        ArchetypeEdges edges = new(tracking);
        Assert.Equal(0, edges.Capacity);
        Assert.Equal(0, tracking.LiveAllocationCount);

        // Consecutive ids, then ids spread over the whole range, so home slots collide and probes chain.
        int[] components = [.. Enumerable.Range(0, 300), .. Enumerable.Range(1, 200).Select(i => i * 4093), int.MaxValue];
        int[] capacities = [8, 16, 32, 64, 128, 256, 512, 1024];
        for (int i = 0; i < components.Length; i++)
        {
            edges.Add(components[i], i);
            Assert.Equal(i + 1, edges.Count);

            // Never more than three quarters full, and the capacity is the smallest power of two that allows it.
            Assert.Contains(edges.Capacity, capacities);
            Assert.True((long)edges.Count * 4 <= (long)edges.Capacity * 3, $"{edges.Count} edges in {edges.Capacity}");
            Assert.True(edges.Capacity == ArchetypeEdges.InitialCapacity || (long)edges.Count * 4 > (long)edges.Capacity / 2 * 3);
        }

        for (int i = 0; i < components.Length; i++)
        {
            Assert.True(edges.TryGet(components[i], out int archetype), $"component {components[i]}");
            Assert.Equal(i, archetype);
        }

        foreach (int absent in (int[])[300, 301, 4094, 1 << 20, int.MaxValue - 1])
        {
            Assert.False(edges.TryGet(absent, out int archetype));
            Assert.Equal(-1, archetype);
        }

        // Growth freed every old array: only the current entries are live.
        Assert.Equal(1, tracking.LiveAllocationCount);
        edges.Dispose();
        edges.Dispose();
        tracking.ThrowIfLeaks();
    }

    /// <summary>Fails the allocation of the first growth (an empty table) or of a later one (six edges in eight entries).</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Growth_AllocationFails_LeavesTableUnchanged(int edgesBefore)
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        FailingAllocator allocator = new(tracking);
        ArchetypeEdges edges = new(allocator);
        for (int i = 0; i < edgesBefore; i++)
        {
            edges.Add(i * 8, i + 100);
        }

        int capacity = edges.Capacity;
        allocator.FailNext = true;
        _ = Assert.Throws<InsufficientMemoryException>(() => edges.Add(1000, 7));

        // The injected failure fired, and the table is as it was.
        Assert.False(allocator.FailNext);
        Assert.Equal(edgesBefore, edges.Count);
        Assert.Equal(capacity, edges.Capacity);
        Assert.False(edges.TryGet(1000, out _));
        for (int i = 0; i < edgesBefore; i++)
        {
            Assert.True(edges.TryGet(i * 8, out int archetype));
            Assert.Equal(i + 100, archetype);
        }

        // Still usable: the next add grows and keeps every edge.
        edges.Add(1000, 7);
        Assert.True(edges.TryGet(1000, out int added));
        Assert.Equal(7, added);
        Assert.Equal(edgesBefore + 1, edges.Count);
        Assert.Equal(1, tracking.LiveAllocationCount);
        edges.Dispose();
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void WithAdded_EdgeGrowthFails_NeighbourExistsAndNextCallCaches()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        FailingAllocator allocator = new(tracking);
        using (EcsWorld world = new(new WorldOptions { Allocator = allocator }))
        {
            _ = ArchetypeTestTypes.Forge(world, 1);
            ArchetypeSet set = world.Archetypes;
            ComponentId c0 = new(0);

            // The empty archetype's first edge allocates its table, and that allocation fails.
            allocator.FailNext = true;
            _ = Assert.Throws<InsufficientMemoryException>(() => set.Empty.WithAdded(c0));
            Assert.False(allocator.FailNext);
            Assert.Equal(0, set.Empty.Edges.Count);
            Assert.Equal(2, set.Count);

            // The next call finds the neighbour the failed one created, and caches it.
            int lookups = set.SignatureLookups;
            Assert.Same(set[1], set.Empty.WithAdded(c0));
            Assert.Equal(lookups + 1, set.SignatureLookups);
            Assert.Same(set[1], set.Empty.WithAdded(c0));
            Assert.Equal(lookups + 1, set.SignatureLookups);
            Assert.Equal(2, set.Count);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Misuse_Throws()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            _ = ArchetypeTestTypes.Forge(world, 2);
            ArchetypeSet set = world.Archetypes;
            Archetype one = set.Empty.WithAdded(new ComponentId(1));
            int lookups = set.SignatureLookups;

            // Adding a present component and removing an absent one throw, and never read or fill the cache: the
            // cached edge for component 1 on the empty archetype is its add edge.
            InvalidOperationException present = Assert.Throws<InvalidOperationException>(() => one.WithAdded(new ComponentId(1)));
            Assert.Contains("already has component 1", present.Message, StringComparison.Ordinal);
            InvalidOperationException absent = Assert.Throws<InvalidOperationException>(() => set.Empty.WithRemoved(new ComponentId(1)));
            Assert.Contains("doesn't have component 1", absent.Message, StringComparison.Ordinal);
            _ = Assert.Throws<InvalidOperationException>(() => one.WithRemoved(new ComponentId(0)));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => one.WithAdded(new ComponentId(2)));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => one.WithAdded(new ComponentId(-1)));
            Assert.Equal(lookups, set.SignatureLookups);
            Assert.Equal(0, one.Edges.Count);
            Assert.Equal(1, set.Empty.Edges.Count);

            // The table itself rejects a repeated edge and negative values, and changes nothing.
            ArchetypeEdges edges = set.Empty.Edges;
            InvalidOperationException repeated = Assert.Throws<InvalidOperationException>(() => edges.Add(1, 5));
            Assert.Contains("already cached, to archetype 1", repeated.Message, StringComparison.Ordinal);
            Assert.Equal("component", Assert.Throws<ArgumentOutOfRangeException>(() => edges.Add(-1, 0)).ParamName);
            Assert.Equal("archetype", Assert.Throws<ArgumentOutOfRangeException>(() => edges.Add(3, -1)).ParamName);
            Assert.Equal("component", Assert.Throws<ArgumentOutOfRangeException>(() => edges.TryGet(-1, out _)).ParamName);
            Assert.Equal(1, edges.Count);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AfterDispose_Throws()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        EcsWorld world = new(new WorldOptions { Allocator = tracking });
        _ = ArchetypeTestTypes.Forge(world, 1);
        Archetype empty = world.Archetypes.Empty;
        Archetype one = empty.WithAdded(new ComponentId(0));

        // The world frees every archetype's edge table.
        world.Dispose();
        tracking.ThrowIfLeaks();

        _ = Assert.Throws<ObjectDisposedException>(() => empty.WithAdded(new ComponentId(0)));
        _ = Assert.Throws<ObjectDisposedException>(() => one.WithRemoved(new ComponentId(0)));
        _ = Assert.Throws<ObjectDisposedException>(() => empty.Edges.Add(5, 0));
        Assert.Equal(0, empty.Edges.Capacity);
        Assert.Equal("allocator", Assert.Throws<ArgumentNullException>(() => new ArchetypeEdges(null!)).ParamName);
    }

    [Fact]
    public void RandomPath_MatchesDirectSignature()
    {
        const int Types = 10;
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            _ = ArchetypeTestTypes.Forge(world, Types);
            ArchetypeSet set = world.Archetypes;

            // Each step toggles one component: removes it if present, adds it otherwise. One thread: the world
            // belongs to this one, and samples share it, so later paths run over cached edges.
            Gen.Int[0, Types - 1].Array[0, 40].Sample(steps =>
            {
                Archetype current = set.Empty;
                SortedSet<int> model = [];
                foreach (int step in steps)
                {
                    var component = new ComponentId(step);
                    if (model.Remove(step))
                    {
                        current = current.WithRemoved(component);
                    }
                    else
                    {
                        current = current.WithAdded(component);
                        _ = model.Add(step);
                    }

                    Assert.Same(set.GetOrCreate(ArchetypeTestTypes.Ids([.. model])), current);
                    Assert.Equal([.. model], current.Signature.Ids.ToArray());
                }
            }, iter: 300, threads: 1);
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>Passes calls through, and throws <see cref="InsufficientMemoryException"/> for one allocation when asked.</summary>
    private sealed class FailingAllocator(INativeAllocator inner) : INativeAllocator
    {
        /// <summary>Set to fail the next allocation; cleared when it fails.</summary>
        public bool FailNext { get; set; }

        public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InsufficientMemoryException("Injected allocation failure.");
            }

            return inner.Allocate(byteCount, alignment, callerFile, callerLine);
        }

        public void Free(void* memory) => inner.Free(memory);
    }
}
