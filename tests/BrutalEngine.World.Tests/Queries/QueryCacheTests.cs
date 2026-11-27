using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Queries;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// The archetype match cache (docs/design/world.md, section 5, "Caching"): incremental refresh over new archetypes
/// only, each data term's column and offset, growth through the world's allocator, failure paths, and no managed
/// allocation. Measured on this thread, with background collection off in this test host (AllocationMeasurementTests).
/// </summary>
public sealed class QueryCacheTests
{
    [Fact]
    public void NewArchetype_MatchedOnRefresh_ScansOnlyNew()
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, _ => false))
        {
            _ = QueryTestSupport.Archetype(world, 0);       // 1
            _ = QueryTestSupport.Archetype(world, 1);       // 2
            _ = QueryTestSupport.Archetype(world, 0, 1);    // 3
            _ = QueryTestSupport.Archetype(world, 5);       // 4
            using ArchetypeMatchCache cache = new(world.Archetypes, QueryTerms.Create(QueryTestSupport.ParseTerms("R0"), QueryTestSupport.MaskBits));

            // A new cache has examined nothing; the first refresh examines every archetype once.
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.ScannedArchetypes);
            Assert.Equal(0, cache.ArchetypesExamined);
            cache.Refresh();
            Assert.Equal([1, 3], QueryTestSupport.MatchedIndices(cache));
            Assert.Equal(5, cache.ScannedArchetypes);
            Assert.Equal(5, cache.ArchetypesExamined);

            // Two new archetypes, one matching: the refresh examines only those two and appends the match.
            _ = QueryTestSupport.Archetype(world, 1, 5);    // 5
            _ = QueryTestSupport.Archetype(world, 0, 5);    // 6
            cache.Refresh();
            Assert.Equal([1, 3, 6], QueryTestSupport.MatchedIndices(cache));
            Assert.Equal(7, cache.ScannedArchetypes);
            Assert.Equal(7, cache.ArchetypesExamined);

            // Nothing new (an existing archetype asked for again creates none): nothing is examined.
            _ = QueryTestSupport.Archetype(world, 0, 1);
            cache.Refresh();
            cache.Refresh();
            Assert.Equal([1, 3, 6], QueryTestSupport.MatchedIndices(cache));
            Assert.Equal(7, cache.ArchetypesExamined);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Refresh_RecordsColumnAndOffsetOfEachDataTerm()
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, id => id == 2))
        {
            // Entry 0 has components 1 and 3. Entry 1 also has the tag 2 between them, so 3 is its third type but its
            // second column, and a wider component 6, so 3's column lands at another offset. Component 0 is in neither.
            Archetype first = QueryTestSupport.Archetype(world, 1, 3);
            Archetype second = QueryTestSupport.Archetype(world, 1, 2, 3, 6);
            Assert.Equal(-1, second.Layout.ColumnOf(1));
            Assert.Equal(1, second.Layout.ColumnOf(2));
            Assert.NotEqual(first.Layout.ColumnOffsets[1], second.Layout.ColumnOffsets[1]);

            // Data terms in order: R3, r2 (absent from entry 0, a tag in entry 1), r0 (absent from both), W1. X5 is
            // excluded and isn't a data term.
            var terms = QueryTerms.Create(QueryTestSupport.ParseTerms("R3 r2 r0 W1 X5"), QueryTestSupport.MaskBits);
            Assert.Equal(4, terms.DataTermCount);
            using ArchetypeMatchCache cache = new(world.Archetypes, terms);
            cache.Refresh();

            Assert.Equal([first.Index, second.Index], QueryTestSupport.MatchedIndices(cache));
            Assert.Same(second, cache.GetArchetype(1));

            // first: columns [1, 3].
            Assert.Equal(1, cache.GetColumn(0, 0));
            Assert.Equal(first.Layout.ColumnOffsets[1], cache.GetOffset(0, 0));
            Assert.Equal(-1, cache.GetColumn(0, 1));
            Assert.Equal(-1, cache.GetOffset(0, 1));
            Assert.Equal(-1, cache.GetColumn(0, 2));
            Assert.Equal(-1, cache.GetOffset(0, 2));
            Assert.Equal(0, cache.GetColumn(0, 3));
            Assert.Equal(first.Layout.ColumnOffsets[0], cache.GetOffset(0, 3));

            // second: columns [1, 3, 6]; the tag 2 has none.
            Assert.Equal(1, cache.GetColumn(1, 0));
            Assert.Equal(second.Layout.ColumnOffsets[1], cache.GetOffset(1, 0));
            Assert.Equal(-1, cache.GetColumn(1, 1));
            Assert.Equal(-1, cache.GetOffset(1, 1));
            Assert.Equal(-1, cache.GetColumn(1, 2));
            Assert.Equal(-1, cache.GetOffset(1, 2));
            Assert.Equal(0, cache.GetColumn(1, 3));
            Assert.Equal(second.Layout.ColumnOffsets[0], cache.GetOffset(1, 3));
            QueryTestSupport.AssertColumns(cache, terms);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Refresh_ManyMatches_GrowsAndKeepsEntries()
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, _ => false))
        {
            var terms = QueryTerms.Create(QueryTestSupport.ParseTerms("W0 r100"), QueryTestSupport.MaskBits);
            using ArchetypeMatchCache cache = new(world.Archetypes, terms);
            long before = tracking.TotalAllocationCount;
            cache.Refresh();
            Assert.Equal(0, cache.Count);
            Assert.Equal(before, tracking.TotalAllocationCount);

            // 20 matching archetypes, alternating with non-matching ones: past 8 and 16 entries the array grows.
            int[] expected = new int[(2 * ArchetypeMatchCache.InitialCapacity) + 4];
            for (int i = 0; i < expected.Length; i++)
            {
                _ = QueryTestSupport.Archetype(world, 1 + i);
                expected[i] = QueryTestSupport.Archetype(world, 0, 1 + i, 100).Index;
                if (i % 7 == 0)
                {
                    cache.Refresh();
                }
            }

            cache.Refresh();
            Assert.Equal(expected, QueryTestSupport.MatchedIndices(cache));
            QueryTestSupport.AssertColumns(cache, terms);

            // Creating archetypes allocates no native memory until they hold a chunk, so every native block since the
            // cache was created is one of its growths: 8, 16 and 32 entries.
            Assert.Equal(3, tracking.TotalAllocationCount - before);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Refresh_GrowthFails_KeepsEarlierMatchesAndRetries()
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        FailingAllocator failing = new(tracking);
        using (EcsWorld world = QueryTestSupport.NewWorld(failing, _ => false))
        {
            var terms = QueryTerms.Create(QueryTestSupport.ParseTerms("R0"), QueryTestSupport.MaskBits);
            using ArchetypeMatchCache cache = new(world.Archetypes, terms);
            int[] expected = new int[ArchetypeMatchCache.InitialCapacity + 2];
            for (int i = 0; i < ArchetypeMatchCache.InitialCapacity; i++)
            {
                expected[i] = QueryTestSupport.Archetype(world, 0, 1 + i).Index;
            }

            cache.Refresh();
            Assert.Equal(ArchetypeMatchCache.InitialCapacity, cache.Count);

            // A non-matching archetype, then two matching ones: the first of those needs the array to grow.
            _ = QueryTestSupport.Archetype(world, 50);
            expected[^2] = QueryTestSupport.Archetype(world, 0, 51).Index;
            expected[^1] = QueryTestSupport.Archetype(world, 0, 52).Index;
            int scanned = cache.ScannedArchetypes;
            failing.FailNext = true;
            _ = Assert.Throws<InsufficientMemoryException>(cache.Refresh);

            // The earlier matches stay, the non-matching archetype counts as scanned, and the failed one doesn't.
            Assert.Equal(expected[..^2], QueryTestSupport.MatchedIndices(cache));
            Assert.Equal(scanned + 1, cache.ScannedArchetypes);
            QueryTestSupport.AssertColumns(cache, terms);

            // The next refresh examines the failed archetype again and finishes, with no duplicate entries.
            cache.Refresh();
            Assert.Equal(expected, QueryTestSupport.MatchedIndices(cache));
            Assert.Equal(world.ArchetypeCount, cache.ScannedArchetypes);
            QueryTestSupport.AssertColumns(cache, terms);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Refresh_FirstGrowthFails_LeavesCacheEmptyAndRetries()
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        FailingAllocator failing = new(tracking);
        using (EcsWorld world = QueryTestSupport.NewWorld(failing, _ => false))
        {
            int match = QueryTestSupport.Archetype(world, 4).Index;
            using ArchetypeMatchCache cache = new(world.Archetypes, QueryTerms.Create(QueryTestSupport.ParseTerms("H4"), QueryTestSupport.MaskBits));
            failing.FailNext = true;
            _ = Assert.Throws<InsufficientMemoryException>(cache.Refresh);
            Assert.Equal(0, cache.Count);
            Assert.Equal(match, cache.ScannedArchetypes);

            cache.Refresh();
            Assert.Equal([match], QueryTestSupport.MatchedIndices(cache));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Refresh_SignatureWidthMismatch_ThrowsAndChangesNothing()
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, _ => false))
        {
            using ArchetypeMatchCache cache = new(world.Archetypes, QueryTerms.Create([], 64));
            ArgumentException ex = Assert.Throws<ArgumentException>(cache.Refresh);
            Assert.Equal("signature", ex.ParamName);
            Assert.Equal(0, cache.Count);
            Assert.Equal(0, cache.ScannedArchetypes);
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(2, 0)]
    [InlineData(int.MinValue, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 2)]
    [InlineData(1, int.MaxValue)]
    public void Accessors_OutOfRange_Throw(int entry, int dataTerm)
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, _ => false))
        {
            _ = QueryTestSupport.Archetype(world, 0, 1);
            _ = QueryTestSupport.Archetype(world, 0, 1, 2);
            using ArchetypeMatchCache cache = new(world.Archetypes, QueryTerms.Create(QueryTestSupport.ParseTerms("R0 W1"), QueryTestSupport.MaskBits));
            cache.Refresh();
            Assert.Equal(2, cache.Count);

            _ = Assert.Throws<ArgumentOutOfRangeException>(() => cache.GetColumn(entry, dataTerm));
            _ = Assert.Throws<ArgumentOutOfRangeException>(() => cache.GetOffset(entry, dataTerm));
            if (dataTerm == 0)
            {
                _ = Assert.Throws<ArgumentOutOfRangeException>(() => cache.GetArchetype(entry));
            }
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Accessors_FirstAndLastIndices_Answer(int entry, int dataTerm)
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, _ => false))
        {
            Archetype[] archetypes = [QueryTestSupport.Archetype(world, 0, 1), QueryTestSupport.Archetype(world, 0, 1, 2)];
            using ArchetypeMatchCache cache = new(world.Archetypes, QueryTerms.Create(QueryTestSupport.ParseTerms("R0 W1"), QueryTestSupport.MaskBits));
            cache.Refresh();

            Assert.Same(archetypes[entry], cache.GetArchetype(entry));
            Assert.Equal(dataTerm, cache.GetColumn(entry, dataTerm));
            Assert.Equal(archetypes[entry].Layout.ColumnOffsets[dataTerm], cache.GetOffset(entry, dataTerm));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Members_AfterDispose_Throw()
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, _ => false))
        {
            _ = QueryTestSupport.Archetype(world, 0);
            ArchetypeMatchCache cache = new(world.Archetypes, QueryTerms.Create(QueryTestSupport.ParseTerms("R0"), QueryTestSupport.MaskBits));
            cache.Refresh();
            Assert.Equal(1, cache.Count);
            cache.Dispose();

            Assert.Equal(0, cache.Count);
            _ = Assert.Throws<ObjectDisposedException>(cache.Refresh);
            _ = Assert.Throws<ObjectDisposedException>(() => cache.GetArchetype(0));
            _ = Assert.Throws<ObjectDisposedException>(() => cache.GetColumn(0, 0));
            _ = Assert.Throws<ObjectDisposedException>(() => cache.GetOffset(0, 0));
            cache.Dispose();
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Constructor_NullArgument_Throws()
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, _ => false))
        {
            var terms = QueryTerms.Create([], QueryTestSupport.MaskBits);
            Assert.Equal("set", Assert.Throws<ArgumentNullException>(() => new ArchetypeMatchCache(null!, terms)).ParamName);
            Assert.Equal("terms", Assert.Throws<ArgumentNullException>(() => new ArchetypeMatchCache(world.Archetypes, null!)).ParamName);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_FreesCache()
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, _ => false))
        {
            for (int i = 0; i < (2 * ArchetypeMatchCache.InitialCapacity) + 1; i++)
            {
                _ = QueryTestSupport.Archetype(world, 7, 8 + i);
            }

            int live = tracking.LiveAllocationCount;
            long bytes = tracking.LiveBytes;
            ArchetypeMatchCache cache = new(world.Archetypes, QueryTerms.Create(QueryTestSupport.ParseTerms("R7 R8"), QueryTestSupport.MaskBits));
            cache.Refresh();
            cache.Refresh();
            Assert.Equal(1, cache.Count);
            for (int i = 1; i < (2 * ArchetypeMatchCache.InitialCapacity) + 1; i++)
            {
                _ = QueryTestSupport.Archetype(world, 7, 8, 8 + i);
            }

            // Grown twice (8, 16, 32 entries): one live block, the last one.
            cache.Refresh();
            Assert.Equal((2 * ArchetypeMatchCache.InitialCapacity) + 1, cache.Count);
            Assert.Equal(live + 1, tracking.LiveAllocationCount);

            cache.Dispose();
            Assert.Equal(live, tracking.LiveAllocationCount);
            Assert.Equal(bytes, tracking.LiveBytes);

            // A cache that never allocated frees nothing.
            ArchetypeMatchCache empty = new(world.Archetypes, QueryTerms.Create(QueryTestSupport.ParseTerms("H120"), QueryTestSupport.MaskBits));
            empty.Refresh();
            empty.Dispose();
            Assert.Equal(live, tracking.LiveAllocationCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Refresh_DoesNotAllocate()
    {
        const int Rounds = 12;
        const int PerRound = 3;
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, id => id == 9))
        {
            var terms = QueryTerms.Create(QueryTestSupport.ParseTerms("R0 C9 w10 X11"), QueryTestSupport.MaskBits);
            using ArchetypeMatchCache cache = new(world.Archetypes, terms);

            // Warm up: JIT every path, the growth included, then measure fresh archetypes round by round.
            _ = QueryTestSupport.Archetype(world, 0, 9);
            cache.Refresh();
            long warm = Read(cache, terms.DataTermCount);

            // The archetypes for each round, made up front: creating them allocates (setup work), so it stays
            // outside the measured windows. Two of each three match; one of those has the optional column.
            var requests = new ComponentId[Rounds * PerRound][];
            for (int i = 0; i < requests.Length; i++)
            {
                int extra = 20 + i;
                requests[i] = (i % PerRound) switch
                {
                    0 => [new ComponentId(0), new ComponentId(9), new ComponentId(extra)],
                    1 => [new ComponentId(0), new ComponentId(9), new ComponentId(10), new ComponentId(extra)],
                    _ => [new ComponentId(0), new ComponentId(9), new ComponentId(11), new ComponentId(extra)],
                };
            }

            long allocated = 0;
            long nativeBefore = tracking.TotalAllocationCount;
            long checksum = warm;
            for (int round = 0; round < Rounds; round++)
            {
                for (int i = 0; i < PerRound; i++)
                {
                    _ = world.Archetypes.GetOrCreate(requests[(round * PerRound) + i]);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();

                // One refresh that appends (and in some rounds grows), then refreshes with nothing new, then reads.
                cache.Refresh();
                for (int repeat = 0; repeat < 50; repeat++)
                {
                    cache.Refresh();
                }

                checksum += Read(cache, terms.DataTermCount);
                allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            }

            Assert.Equal(0, allocated);

            // The windows did append and grow: 1 + 24 entries, so the array went 8, 16 and 32 in them.
            Assert.Equal(1 + (Rounds * 2), cache.Count);
            Assert.True(tracking.TotalAllocationCount - nativeBefore >= 2, "the measured refreshes never grew the array");
            Assert.NotEqual(0, checksum);
            QueryTestSupport.AssertColumns(cache, terms);

            // Dispose, which frees the grown array, allocates nothing managed either (warmed up on a first cache).
            ArchetypeMatchCache warmUp = new(world.Archetypes, terms);
            warmUp.Refresh();
            warmUp.Dispose();
            ArchetypeMatchCache second = new(world.Archetypes, terms);
            second.Refresh();
            int live = tracking.LiveAllocationCount;
            long disposeBefore = GC.GetAllocatedBytesForCurrentThread();
            second.Dispose();
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - disposeBefore);
            Assert.Equal(live - 1, tracking.LiveAllocationCount);
        }

        tracking.ThrowIfLeaks();
    }

    // Reads every entry through every accessor, folding the values into a checksum.
    private static long Read(ArchetypeMatchCache cache, int dataTerms)
    {
        long sum = cache.Count + cache.ScannedArchetypes + cache.ArchetypesExamined;
        for (int entry = 0; entry < cache.Count; entry++)
        {
            sum += cache.GetArchetype(entry).Index;
            for (int term = 0; term < dataTerms; term++)
            {
                sum += cache.GetColumn(entry, term) + cache.GetOffset(entry, term);
            }
        }

        return sum;
    }
}
