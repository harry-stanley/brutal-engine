using System;
using System.Threading;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using Xunit;

namespace BrutalEngine.World.Tests.Archetypes;

public sealed class ArchetypeSetTests
{
    [Fact]
    public void Deterministic_Indices()
    {
        // Forty requests: new sets, repeats and reorderings of earlier ones. Past 16 archetypes the list and the map grow.
        int[][] requests = new int[40][];
        for (int i = 0; i < requests.Length; i++)
        {
            requests[i] = (i % 5) switch
            {
                0 => [i % 9],
                1 => [i % 9, (i + 3) % 9],
                2 => [(i + 3) % 9, i % 9, i % 9],
                3 => [i % 9, (i + 1) % 9, (i + 4) % 9],
                _ => [],
            };
        }

        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld first = new(new WorldOptions { Allocator = tracking }))
        using (EcsWorld second = new(new WorldOptions { Allocator = tracking }))
        {
            int[] firstIndices = Run(first, requests);
            int[] secondIndices = Run(second, requests);

            // The same requests give the same indices, and a new set gets the next index (creation order).
            Assert.Equal(firstIndices, secondIndices);
            int next = 1;
            for (int i = 0; i < requests.Length; i++)
            {
                if (firstIndices[i] == next)
                {
                    next++;
                }
                else
                {
                    Assert.InRange(firstIndices[i], 0, next - 1);
                }
            }

            Assert.True(next > ArchetypeSet.InitialCapacity + 1, $"only {next} archetypes: the tables never grew");
            Assert.Equal(next, first.ArchetypeCount);
            Assert.Equal(next, second.ArchetypeCount);

            // After growth, every request still finds the archetype it got the first time.
            Assert.Equal(firstIndices, Run(first, requests));
            Assert.Equal(next, first.ArchetypeCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Constructor_EmptyArchetypeIsIndexZero()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking, ChunkBytes = 32 * 1024 }))
        {
            ArchetypeSet set = world.Archetypes;
            Assert.Equal(1, world.ArchetypeCount);
            Assert.Equal(0, set.Empty.Index);
            Assert.Same(set.Empty, set[0]);
            Assert.Equal(0, set.Empty.Signature.Count);
            Assert.Equal(32 * 1024 / ChunkLayout.EntityIdBytes, set.Empty.Layout.Capacity);
            Assert.Same(set.Empty, set.GetOrCreate([]));

            // Only the entity table's two arrays: creating an archetype allocates no native memory.
            _ = set.GetOrCreate(ArchetypeTestTypes.Ids(ArchetypeTestTypes.Forge(world, 1).Index));
            Assert.Equal(2, tracking.LiveAllocationCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void GetOrCreate_Existing_DoesNotAllocate()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            _ = ArchetypeTestTypes.Forge(world, 4);
            ArchetypeSet set = world.Archetypes;
            Archetype three = set.GetOrCreate(ArchetypeTestTypes.Ids(0, 1, 2));
            _ = set.GetOrCreate(ArchetypeTestTypes.Ids(0, 2));
            ComponentId[] request = ArchetypeTestTypes.Ids(2, 0, 1, 0);

            // Warm up: the id buffer has held the request once.
            Assert.Same(three, set.GetOrCreate(request));
            int lookups = set.SignatureLookups;
            int count = set.Count;

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                _ = set.GetOrCreate(request);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);

            // Each call searched the map once and found the existing archetype.
            Assert.Equal(lookups + 1000, set.SignatureLookups);
            Assert.Equal(count, set.Count);
            Assert.Same(three, set.GetOrCreate(request));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void GetOrCreate_RowTooWide_LeavesSetUnchangedAndUsable()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking, ChunkBytes = 16 * 1024 }))
        {
            // 16 rows of 8 + 1,000 bytes fit 16 KiB; adding 24 bytes takes 16,512 bytes, which doesn't.
            int big = ArchetypeTestTypes.Forge(world, 1, size: 1000, alignment: 8).Index;
            int small = ArchetypeTestTypes.Forge(world, 1, size: 24, alignment: 8).Index;
            ArchetypeSet set = world.Archetypes;
            Archetype bigOnly = set.GetOrCreate(ArchetypeTestTypes.Ids(big));
            int count = set.Count;

            InvalidOperationException exception =
                Assert.Throws<InvalidOperationException>(() => set.GetOrCreate(ArchetypeTestTypes.Ids(small, big)));
            Assert.Contains("Tests.Archetype.C0, Tests.Archetype.C1", exception.Message, StringComparison.Ordinal);
            Assert.Equal(count, set.Count);

            // No index was used up, and the set still finds and creates archetypes.
            Assert.Same(bigOnly, set.GetOrCreate(ArchetypeTestTypes.Ids(big)));
            Archetype smallOnly = set.GetOrCreate(ArchetypeTestTypes.Ids(small));
            Assert.Equal(count, smallOnly.Index);
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void GetOrCreate_UnregisteredId_ThrowsAndLeavesSetUnchanged(int index)
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            _ = ArchetypeTestTypes.Forge(world, 3);
            ArchetypeSet set = world.Archetypes;
            _ = set.GetOrCreate(ArchetypeTestTypes.Ids(1));

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => set.GetOrCreate(ArchetypeTestTypes.Ids(0, index)));

            Assert.Equal(2, set.Count);
            Assert.Equal(2, set.GetOrCreate(ArchetypeTestTypes.Ids(0, 2)).Index);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void GetOrCreate_HashCollision_KeepsArchetypesApart()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking, MaxComponentTypes = 4096 }))
        {
            _ = ArchetypeTestTypes.Forge(world, 4070);
            ArchetypeSet set = world.Archetypes;

            // Two sets with the same hash (ArchetypeSignatureTests.Equality_HashCollision_ComparesIds): the second
            // probes past the first, and each request still finds its own archetype.
            Archetype first = set.GetOrCreate(ArchetypeTestTypes.Ids(163, 215, 1190, 1580, 2107, 2340));
            Archetype second = set.GetOrCreate(ArchetypeTestTypes.Ids(811, 1099, 2434, 3274, 4069));

            Assert.Equal(first.Signature.Hash, second.Signature.Hash);
            Assert.NotSame(first, second);
            Assert.Same(first, set.GetOrCreate(ArchetypeTestTypes.Ids(2340, 163, 215, 1190, 1580, 2107)));
            Assert.Same(second, set.GetOrCreate(ArchetypeTestTypes.Ids(4069, 811, 1099, 2434, 3274)));
            Assert.Equal(3, set.Count);
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Indexer_OutOfRange_Throws(int index)
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => world.Archetypes[index]);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_ReturnsChunks_ThenLookupsThrow()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        EcsWorld world = new(new WorldOptions { Allocator = tracking });
        _ = ArchetypeTestTypes.Forge(world, 2);
        ArchetypeSet set = world.Archetypes;
        Archetype archetype = set.GetOrCreate(ArchetypeTestTypes.Ids(0, 1));
        _ = archetype.Chunks.AddChunk(1);
        _ = set.Empty.Chunks.AddChunk(1);

        // The world disposes the set before its pool, so the blocks go back and the pool frees them.
        world.Dispose();
        tracking.ThrowIfLeaks();

        Assert.Equal(0, set.Count);
        _ = Assert.Throws<ObjectDisposedException>(() => set.GetOrCreate([]));
        _ = Assert.Throws<ObjectDisposedException>(() => set[0]);
        set.Dispose();
    }

    [Fact]
    public void Dispose_ArchetypeThrows_DisposesTheRestAndRethrows()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            _ = ArchetypeTestTypes.Forge(world, 1);
            int baseline = tracking.LiveAllocationCount;
            ChunkPool pool = new(tracking, 16 * 1024);
            ArchetypeSet set = new(tracking, pool, world.Components, 256);
            Archetype empty = set.Empty;
            Archetype one = set.GetOrCreate(ArchetypeTestTypes.Ids(0));
            _ = empty.Chunks.AddChunk(1);
            _ = one.Chunks.AddChunk(1);
            nint[] blocks = [BlockOf(empty), BlockOf(one)];

            // Misuse: the pool goes first, so both tables throw; the second is still disposed and the first error
            // surfaces.
            pool.Dispose();
            _ = Assert.Throws<ObjectDisposedException>(set.Dispose);
            Assert.Equal(0, empty.Chunks.ChunkCount);
            Assert.Equal(0, one.Chunks.ChunkCount);

            // Only the two blocks are left: both tables freed their arrays.
            Assert.Equal(baseline + blocks.Length, tracking.LiveAllocationCount);
            foreach (nint block in blocks)
            {
                Free(tracking, block);
            }
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void ArchetypeCount_AfterDispose_Throws()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        EcsWorld world = new(new WorldOptions { Allocator = tracking });
        world.Dispose();

        ObjectDisposedException exception = Assert.Throws<ObjectDisposedException>(() => world.ArchetypeCount);

        Assert.Equal(typeof(EcsWorld).FullName, exception.ObjectName);
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void ArchetypeCount_OtherThread_Throws()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            Exception? failure = null;
            Thread thread = new(() =>
            {
                try
                {
                    _ = world.ArchetypeCount;
                }
                catch (Exception ex)
                {
                    // Caught here, so it can't escape the thread and take down the test host.
                    failure = ex;
                }
            });
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The other thread didn't finish.");

            Assert.Equal("EcsWorld.ArchetypeCount", Assert.IsType<WrongThreadException>(failure).ApiName);
        }

        tracking.ThrowIfLeaks();
    }

    private static int[] Run(EcsWorld world, int[][] requests)
    {
        if (world.Components.Count == 0)
        {
            _ = ArchetypeTestTypes.Forge(world, 9);
        }

        int[] indices = new int[requests.Length];
        for (int i = 0; i < requests.Length; i++)
        {
            indices[i] = world.Archetypes.GetOrCreate(ArchetypeTestTypes.Ids(requests[i])).Index;
        }

        return indices;
    }

    private static unsafe nint BlockOf(Archetype archetype) => (nint)archetype.Chunks.GetData(0);

    // Sound: the block came from this tracking allocator, through the pool, and was never freed.
    private static unsafe void Free(TrackingAllocator tracking, nint block) => tracking.Free((void*)block);
}
