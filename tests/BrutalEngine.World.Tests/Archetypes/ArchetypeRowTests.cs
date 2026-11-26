using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using CsCheck;
using Xunit;

namespace BrutalEngine.World.Tests.Archetypes;

public sealed unsafe class ArchetypeRowTests
{
    // A 1,000-byte and a 4-byte column in 16 KiB chunks: about 16 rows per chunk, so a few dozen rows span chunks.
    private const int ChunkBytes = 16 * 1024;
    private const int BigSize = 1000;

    [Fact]
    public void DenseInvariant_Holds()
    {
        // Mostly appends, so paths span several chunks; removals pick any row, the last one more often.
        Gen.Select(Gen.Int[0, 9], Gen.Int[0, 100_000]).Array[0, 150].Sample(steps =>
        {
            TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
            using (EcsWorld world = NewWorld(tracking))
            {
                Archetype archetype = TwoColumns(world);
                List<Entity> model = [];
                uint next = 0;
                foreach ((int kind, int pick) in steps)
                {
                    if (kind < 6 || model.Count == 0)
                    {
                        var entity = new Entity(next++, 1);
                        (int chunk, int row) = archetype.AppendRow(entity, tick: 3);
                        Assert.Equal((model.Count / archetype.Layout.Capacity, model.Count % archetype.Layout.Capacity), (chunk, row));
                        WriteValues(archetype, chunk, row, entity.Index);
                        model.Add(entity);
                    }
                    else
                    {
                        int index = kind == 9 ? model.Count - 1 : pick % model.Count;
                        int capacity = archetype.Layout.Capacity;
                        Entity moved = archetype.SwapRemove(index / capacity, index % capacity);

                        // The last row fills the hole; removing the last row itself moves nothing.
                        Entity last = model[^1];
                        model[index] = last;
                        model.RemoveAt(model.Count - 1);
                        Assert.Equal(index == model.Count ? Entity.Null : last, moved);
                    }

                    AssertDense(world, archetype, model);
                }
            }

            tracking.ThrowIfLeaks();
        }, iter: 100, threads: 1);
    }

    [Fact]
    public void EmptiedLastChunk_ReturnedToPool()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            Archetype archetype = TwoColumns(world);
            int capacity = archetype.Layout.Capacity;
            for (uint i = 0; i <= capacity; i++)
            {
                (int chunk, int row) = archetype.AppendRow(new Entity(i, 1), tick: 1);
                WriteValues(archetype, chunk, row, i);
            }

            Assert.Equal(2, archetype.Chunks.ChunkCount);
            nint block = (nint)archetype.Chunks.GetData(1);
            long allocations = tracking.TotalAllocationCount;

            // Removing row 3 moves the only row of the second chunk into it, and that chunk goes back to the pool.
            Assert.Equal(new Entity((uint)capacity, 1), archetype.SwapRemove(0, 3));
            Assert.Equal(1, archetype.Chunks.ChunkCount);
            Assert.Equal(1, world.ChunkPool.RentedCount);
            Assert.Equal(1, world.ChunkPool.PooledCount);
            AssertValues(archetype, 0, 3, (uint)capacity);

            // The next chunk reuses that block, with no allocator call.
            Assert.Equal((1, 0), archetype.AppendRow(new Entity(99, 1), tick: 2));
            Assert.Equal(block, (nint)archetype.Chunks.GetData(1));
            Assert.Equal(0, world.ChunkPool.PooledCount);
            Assert.Equal(allocations, tracking.TotalAllocationCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void ExistingArchetypes_DoNotAllocate()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            Archetype archetype = TwoColumns(world);
            ComponentId small = new(1);
            Archetype bigOnly = archetype.WithRemoved(small);
            ComponentId[] request = ArchetypeTestTypes.Ids(1, 0, 1);
            int capacity = archetype.Layout.Capacity;
            for (uint i = 0; i < capacity; i++)
            {
                _ = archetype.AppendRow(new Entity(i, 1), tick: 1);
            }

            // Warm up: a second chunk has been rented and returned, and every path has run once.
            Churn(world, archetype, bigOnly, small, request);
            long nativeAllocations = tracking.TotalAllocationCount;

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                Churn(world, archetype, bigOnly, small, request);
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Equal(0, allocated);
            Assert.Equal(nativeAllocations, tracking.TotalAllocationCount);
            Assert.Equal(capacity, archetype.RowCount);
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>
    /// Fails the new chunk's first allocation: the block for the first chunk of an empty archetype or the second of a
    /// full first chunk, or, with the chunk arrays full and a block in the pool, the grown chunk arrays.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(ChunkTable.InitialCapacity)]
    public void AppendRow_AllocationFails_LeavesArchetypeUnchanged(int fullChunks)
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        FailingAllocator allocator = new(tracking);
        using (EcsWorld world = NewWorld(allocator))
        {
            Archetype archetype = TwoColumns(world);
            List<Entity> model = [];
            int rows = fullChunks * archetype.Layout.Capacity;
            for (uint i = 0; i < rows; i++)
            {
                var entity = new Entity(i, 1);
                (int chunk, int row) = archetype.AppendRow(entity, tick: 1);
                WriteValues(archetype, chunk, row, i);
                model.Add(entity);
            }

            bool arraysFull = fullChunks == ChunkTable.InitialCapacity;
            if (arraysFull)
            {
                // A block in the pool, so the next append rents it and its only allocation is the grown chunk arrays.
                world.ChunkPool.Return(world.ChunkPool.Rent());
                Assert.Equal(archetype.Chunks.ChunkCount, archetype.Chunks.Capacity);
            }

            allocator.FailNext = true;
            _ = Assert.Throws<InsufficientMemoryException>(() => archetype.AppendRow(new Entity(500, 1), tick: 2));

            // The injected failure fired, and nothing changed: a rented block went back to the pool.
            Assert.False(allocator.FailNext);
            Assert.Equal(fullChunks, archetype.Chunks.ChunkCount);
            Assert.Equal(arraysFull ? 1 : 0, world.ChunkPool.PooledCount);
            AssertDense(world, archetype, model);

            // Still usable: the next append opens the chunk.
            var next = new Entity(500, 1);
            (int newChunk, int newRow) = archetype.AppendRow(next, tick: 2);
            Assert.Equal((rows / archetype.Layout.Capacity, 0), (newChunk, newRow));
            WriteValues(archetype, newChunk, newRow, next.Index);
            model.Add(next);
            AssertDense(world, archetype, model);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void ChunkCount_SumsArchetypes()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            Archetype first = TwoColumns(world);
            Archetype second = first.WithRemoved(new ComponentId(1));
            Assert.Equal(0, world.ChunkCount);

            for (uint i = 0; i <= first.Layout.Capacity; i++)
            {
                _ = first.AppendRow(new Entity(i, 1), tick: 1);
            }

            _ = second.AppendRow(new Entity(1000, 1), tick: 1);
            _ = world.Archetypes.Empty.AppendRow(new Entity(1001, 1), tick: 1);
            Assert.Equal(4, world.ChunkCount);

            // Emptied chunks go back to the pool and stop counting.
            Assert.Equal(Entity.Null, second.SwapRemove(0, 0));
            Assert.Equal(Entity.Null, first.SwapRemove(1, 0));
            Assert.Equal(2, world.ChunkCount);
            Assert.Equal(2, world.ChunkPool.PooledCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void ChunkCount_AfterDisposeOrOtherThread_Throws()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        EcsWorld world = NewWorld(tracking);
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                _ = world.ChunkCount;
            }
            catch (Exception ex)
            {
                // Caught here, so it can't escape the thread and take down the test host.
                failure = ex;
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The other thread didn't finish.");
        Assert.Equal("EcsWorld.ChunkCount", Assert.IsType<WrongThreadException>(failure).ApiName);

        world.Dispose();
        Assert.Equal(typeof(EcsWorld).FullName, Assert.Throws<ObjectDisposedException>(() => world.ChunkCount).ObjectName);
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_ResetsRowCount()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        EcsWorld world = NewWorld(tracking);
        Archetype archetype = TwoColumns(world);
        for (uint i = 0; i <= archetype.Layout.Capacity; i++)
        {
            _ = archetype.AppendRow(new Entity(i, 1), tick: 1);
        }

        Assert.Equal(archetype.Layout.Capacity + 1, archetype.RowCount);

        // The world disposes the archetype: no chunks and no rows are left.
        world.Dispose();
        tracking.ThrowIfLeaks();

        Assert.Equal(0, archetype.RowCount);
        Assert.Equal(0, archetype.Chunks.ChunkCount);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 2)]
    public void SwapRemove_NotALiveRow_ThrowsAndChangesNothing(int chunk, int row)
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            Archetype archetype = TwoColumns(world);
            List<Entity> model = [new Entity(0, 1), new Entity(1, 1)];
            for (int i = 0; i < model.Count; i++)
            {
                _ = archetype.AppendRow(model[i], tick: 1);
                WriteValues(archetype, 0, i, model[i].Index);
            }

            // Row 2 is inside the chunk's capacity, but not live.
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => archetype.SwapRemove(chunk, row));
            AssertDense(world, archetype, model);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AppendRow_NullEntity_Throws()
    {
        TrackingAllocator tracking = ArchetypeTestTypes.NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            Archetype archetype = TwoColumns(world);

            Assert.Equal("entity", Assert.Throws<ArgumentException>(() => archetype.AppendRow(Entity.Null, tick: 1)).ParamName);
            Assert.Equal(0, archetype.RowCount);
            Assert.Equal(0, archetype.Chunks.ChunkCount);
        }

        tracking.ThrowIfLeaks();
    }

    private static EcsWorld NewWorld(INativeAllocator allocator) => new(new WorldOptions { Allocator = allocator, ChunkBytes = ChunkBytes });

    // Component 0: 1,000 bytes aligned to 8; component 1: 4 bytes.
    private static Archetype TwoColumns(EcsWorld world)
    {
        _ = ArchetypeTestTypes.Forge(world, 1, size: BigSize, alignment: 8);
        _ = ArchetypeTestTypes.Forge(world, 1, size: 4, alignment: 4);
        Archetype archetype = world.Archetypes.GetOrCreate(ArchetypeTestTypes.Ids(0, 1));
        Assert.InRange(archetype.Layout.Capacity, ChunkLayout.MinCapacity, 20);
        return archetype;
    }

    // With the first chunk full: opens a second chunk and empties it again, and follows cached edges and a set lookup.
    private static void Churn(EcsWorld world, Archetype archetype, Archetype bigOnly, ComponentId small, ComponentId[] request)
    {
        _ = archetype.AppendRow(new Entity(7, 1), tick: 2);

        // The new row fills row 0, and the emptied second chunk goes back to the pool.
        _ = archetype.SwapRemove(0, 0);
        _ = archetype.WithRemoved(small).WithAdded(small);
        _ = bigOnly.WithAdded(small);
        _ = world.Archetypes.GetOrCreate(request);
    }

    // The big column holds the value in its first and (complemented) last 4 bytes, the small column its complement, so
    // a move that copies a column short or into the wrong place shows.
    private static void WriteValues(Archetype archetype, int chunk, int row, uint value)
    {
        byte* data = archetype.Chunks.GetData(chunk);
        ReadOnlySpan<int> offsets = archetype.Layout.ColumnOffsets;
        byte* big = data + offsets[0] + ((nint)row * BigSize);
        *(uint*)big = value;
        *(uint*)(big + BigSize - 4) = value ^ 0xFFFFu;
        *(uint*)(data + offsets[1] + ((nint)row * 4)) = ~value;
    }

    private static void AssertValues(Archetype archetype, int chunk, int row, uint value)
    {
        byte* data = archetype.Chunks.GetData(chunk);
        ReadOnlySpan<int> offsets = archetype.Layout.ColumnOffsets;
        byte* big = data + offsets[0] + ((nint)row * BigSize);
        Assert.Equal(value, *(uint*)big);
        Assert.Equal(value ^ 0xFFFFu, *(uint*)(big + BigSize - 4));
        Assert.Equal(~value, *(uint*)(data + offsets[1] + ((nint)row * 4)));
    }

    // Every chunk but the last is full, the last isn't empty, and row i holds the model's i-th entity and its values.
    private static void AssertDense(EcsWorld world, Archetype archetype, List<Entity> model)
    {
        int capacity = archetype.Layout.Capacity;
        int chunks = (model.Count + capacity - 1) / capacity;
        ChunkTable table = archetype.Chunks;
        Assert.Equal(chunks, table.ChunkCount);
        Assert.Equal(model.Count, archetype.RowCount);
        Assert.Equal(chunks, world.ChunkCount);
        Assert.Equal(chunks, world.ChunkPool.RentedCount);
        for (int i = 0; i < model.Count; i++)
        {
            int chunk = i / capacity;
            int row = i % capacity;
            Assert.Equal(chunk == chunks - 1 ? model.Count - (chunk * capacity) : capacity, table.GetCount(chunk));
            Assert.Equal(model[i], ((Entity*)table.GetData(chunk))[row]);
            AssertValues(archetype, chunk, row, model[i].Index);
        }
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
