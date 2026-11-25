using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using BrutalEngine.Core.Memory;
using CsCheck;
using Xunit;

namespace BrutalEngine.Core.Tests.Memory;

public sealed unsafe class TrackingAllocatorTests
{
    [Fact]
    public void Constructor_ValidatesArguments()
    {
        HeapAllocator heap = new();

        _ = Assert.Throws<ArgumentNullException>(() => new TrackingAllocator(null!, "x"));
        _ = Assert.Throws<ArgumentNullException>(() => new TrackingAllocator(heap, null!));
        _ = Assert.Throws<ArgumentException>(() => new TrackingAllocator(heap, " "));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new TrackingAllocator(heap, "x", -1));
    }

    [Fact]
    public void AllocateAndFree_UpdateCounters()
    {
        TrackingAllocator tracker = new(new HeapAllocator(), "Test");

        void* a = tracker.Allocate(100, 8);
        void* b = tracker.Allocate(28, 4);
        Assert.Equal(2, tracker.LiveAllocationCount);
        Assert.Equal(128, tracker.LiveBytes);

        tracker.Free(a);
        Assert.Equal(1, tracker.LiveAllocationCount);
        Assert.Equal(28, tracker.LiveBytes);
        Assert.Equal(128, tracker.PeakLiveBytes);
        Assert.Equal(2, tracker.TotalAllocationCount);

        tracker.Free(b);
        Assert.Equal(0, tracker.LiveAllocationCount);
        Assert.Equal(0, tracker.LiveBytes);
        tracker.ThrowIfLeaks();
    }

    [Fact]
    public void Leak_IsReportedWithCallSite()
    {
        TrackingAllocator tracker = new(new HeapAllocator(), "Renderer");

        void* leaked = tracker.Allocate(64, 16);
        int line = 0;
        try
        {
            NativeMemoryLeakException exception = Assert.Throws<NativeMemoryLeakException>(tracker.ThrowIfLeaks);

            NativeAllocationInfo info = Assert.Single(tracker.GetLiveAllocations());
            line = info.CallerLine;
            Assert.Equal(1, exception.LeakedAllocations);
            Assert.Equal(64, exception.LeakedBytes);
            Assert.EndsWith(nameof(TrackingAllocatorTests) + ".cs", info.CallerFile, StringComparison.Ordinal);
            Assert.True(line > 0);
            Assert.Contains("Allocator 'Renderer': 1 live allocation(s), 64 byte(s).", exception.Message, StringComparison.Ordinal);
            Assert.Contains($"{nameof(TrackingAllocatorTests)}.cs:{line}", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            tracker.Free(leaked);
        }
    }

    [Fact]
    public void CallSite_IsRecordedThroughTheInterface()
    {
        TrackingAllocator tracker = new(new HeapAllocator(), "Test");

        void* block = AllocateThroughInterface(tracker);
        try
        {
            NativeAllocationInfo info = Assert.Single(tracker.GetLiveAllocations());
            Assert.EndsWith(nameof(TrackingAllocatorTests) + ".cs", info.CallerFile, StringComparison.Ordinal);
            Assert.True(info.CallerLine > 0);
        }
        finally
        {
            tracker.Free(block);
        }
    }

    [Fact]
    public void DoubleFree_ThrowsAndLeavesStateUnchanged()
    {
        CountingAllocator inner = new();
        TrackingAllocator tracker = new(inner, "Test");
        void* block = tracker.Allocate(32, 8);
        void* other = tracker.Allocate(32, 8);
        tracker.Free(block);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => tracker.Free(block));

        Assert.Contains("not a live allocation", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, inner.Frees);
        Assert.Equal(1, tracker.LiveAllocationCount);
        tracker.Free(other);
    }

    [Fact]
    public void ForeignPointer_ThrowsWithoutReachingInnerAllocator()
    {
        HeapAllocator heap = new();
        CountingAllocator inner = new();
        TrackingAllocator tracker = new(inner, "Test");
        void* foreign = heap.Allocate(16, 8);
        try
        {
            _ = Assert.Throws<InvalidOperationException>(() => tracker.Free(foreign));
            Assert.Equal(0, inner.Frees);
        }
        finally
        {
            heap.Free(foreign);
        }
    }

    [Fact]
    public void InvalidRequest_ThrowsBeforeReachingInnerAllocator()
    {
        CountingAllocator inner = new();
        TrackingAllocator tracker = new(inner, "Test");

        _ = Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Allocate(0, 8));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => tracker.Allocate(8, 3));
        Assert.Equal(0, inner.Allocations);
    }

    [Fact]
    public void LeakReport_ListsOldestFirstAndTruncatesAfterFifty()
    {
        TrackingAllocator tracker = new(new HeapAllocator(), "Test");
        nint[] blocks = new nint[60];
        for (int i = 0; i < blocks.Length; i++)
        {
            blocks[i] = (nint)tracker.Allocate((nuint)(i + 1), 8);
        }

        try
        {
            string report = tracker.CreateLeakReport();

            Assert.StartsWith("Allocator 'Test': 60 live allocation(s), 1830 byte(s).", report, StringComparison.Ordinal);
            Assert.Contains("#1: 1 byte(s)", report, StringComparison.Ordinal);
            Assert.Contains("#50: 50 byte(s)", report, StringComparison.Ordinal);
            Assert.DoesNotContain("#51:", report, StringComparison.Ordinal);
            Assert.EndsWith("... and 10 more.", report, StringComparison.Ordinal);
        }
        finally
        {
            foreach (nint block in blocks)
            {
                tracker.Free((void*)block);
            }
        }

        tracker.ThrowIfLeaks();
    }

    [Fact]
    public void RandomAllocateFreeSequences_KeepCountersConsistent()
    {
        Gen.Int[1, 512].Array[1, 64].Select(Gen.Int.Array[0, 64]).Sample((sizes, freeOrder) =>
        {
            TrackingAllocator tracker = new(new HeapAllocator(), "Property");
            nint[] blocks = new nint[sizes.Length];
            long expected = 0;
            for (int i = 0; i < sizes.Length; i++)
            {
                blocks[i] = (nint)tracker.Allocate((nuint)sizes[i], 8);
                expected += sizes[i];
            }

            bool consistent = tracker.LiveBytes == expected && tracker.LiveAllocationCount == sizes.Length;
            bool[] freed = new bool[sizes.Length];
            foreach (int pick in freeOrder)
            {
                int index = (int)((uint)pick % (uint)sizes.Length);
                if (!freed[index])
                {
                    tracker.Free((void*)blocks[index]);
                    freed[index] = true;
                    expected -= sizes[index];
                    consistent &= tracker.LiveBytes == expected;
                }
            }

            for (int i = 0; i < sizes.Length; i++)
            {
                if (!freed[i])
                {
                    tracker.Free((void*)blocks[i]);
                }
            }

            return consistent && tracker.LiveBytes == 0 && tracker.LiveAllocationCount == 0;
        });
    }

    [Fact]
    public void ConcurrentUse_IsThreadSafe()
    {
        TrackingAllocator tracker = new(new HeapAllocator(), "Concurrent");

        _ = Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = TestContext.Current.CancellationToken }, _ =>
        {
            for (int i = 0; i < 2000; i++)
            {
                tracker.Free(tracker.Allocate((nuint)((i % 64) + 1), 16));
            }
        });

        Assert.Equal(16000, tracker.TotalAllocationCount);
        tracker.ThrowIfLeaks();
    }

    [Fact]
    public void SteadyState_DoesNotAllocateManagedMemory()
    {
        TrackingAllocator tracker = new(new HeapAllocator(), "Test", initialCapacity: 4);
        tracker.Free(tracker.Allocate(64, 16));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            tracker.Free(tracker.Allocate(64, 16));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // Generic so the call binds to INativeAllocator.Allocate (and its caller attributes), not the class method.
    private static void* AllocateThroughInterface<TAllocator>(TAllocator allocator)
        where TAllocator : INativeAllocator => allocator.Allocate(8, 8);

    /// <summary>Counts calls that reach it, to prove the tracker rejects bad requests before passing them on.</summary>
    private sealed class CountingAllocator : INativeAllocator
    {
        private readonly HeapAllocator _heap = new();

        public int Allocations { get; private set; }

        public int Frees { get; private set; }

        public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0)
        {
            Allocations++;
            return _heap.Allocate(byteCount, alignment, callerFile, callerLine);
        }

        public void Free(void* memory)
        {
            Frees++;
            _heap.Free(memory);
        }
    }
}
