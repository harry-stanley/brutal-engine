using System;
using System.Collections.Concurrent;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Threading;
using Xunit;

namespace HelloWindow.Tests;

/// <summary>
/// Checks that this test host measures allocations exactly, so the zero-allocation tests (FrameResultPolicyTests) can't
/// fail because of collections that other tests running in parallel cause (docs/05-testing-strategy.md, "Allocation
/// tests"). A copy of BrutalEngine.Core.Tests' guard: each test project runs in its own host, with its own GC settings.
/// </summary>
public sealed class AllocationMeasurementTests
{
    // Small objects kept alive while the collection runs, so a background collection's concurrent mark phase is
    // still running when the measured window opens.
    private const int LiveObjects = 1_000_000;

    // Bound every loop that waits on the garbage collector, so a broken setup fails instead of hanging the run.
    private const int MaxLargeAllocations = 100_000;
    private const int MaxGen2Attempts = 10;

    [Fact]
    public void GetAllocatedBytesForCurrentThread_Gen2CollectionEndsDuringWindow_CountsNoBytes()
    {
        object?[] live = new object?[LiveObjects];
        for (int i = 0; i < live.Length; i++)
        {
            live[i] = new object();
        }

        // Starts with an empty gen0, so no gen0 collection runs during the window: one would retire this thread's
        // allocation context early, and count it correctly.
        GC.Collect();
        ConcurrentQueue<Exception> failures = new();
        Gen2Collector collector = new();
        Thread thread = new(() =>
        {
            try
            {
                collector.Run();
            }
            catch (Exception ex)
            {
                failures.Enqueue(ex);
            }
            finally
            {
                collector.Finish();
            }
        });
        thread.Start();
        collector.WaitUntilReady();

        // A collection retires every thread's allocation context when it starts, and counts that correctly.
        // Allocate after that, so this thread holds a partly used context while the collection ends.
        _ = NewObject();
        long before = GC.GetAllocatedBytesForCurrentThread();
        collector.OpenWindow();
        thread.Join();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(live);

        Assert.Empty(failures);
        Assert.True(collector.CollectionEndedInWindow, "No gen2 collection ended inside the measured window.");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void TestHost_BackgroundGarbageCollection_IsDisabled()
    {
        // The test above passes vacuously in a run where no background collection happens to end inside its window,
        // so check the setting itself too. Two checks: the configuration switch that the project's
        // ConcurrentGarbageCollection property writes to the runtime config, so dropping the property fails here even
        // if something else (DOTNET_gcConcurrent=0) happens to turn background collection off; and the collector's
        // effective mode, which is Interactive when background collection is on and Batch when it's off, so
        // anything that turns it back on (DOTNET_gcConcurrent=1, say) fails here too.
        Assert.Equal("false", AppContext.GetData("System.GC.Concurrent") as string, ignoreCase: true);
        Assert.Equal(GCLatencyMode.Batch, GCSettings.LatencyMode);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object NewObject() => new();

    /// <summary>
    /// Makes a gen2 collection end inside the measuring thread's window. It drives gen2 collections with
    /// large-object allocations, because that's how the runtime starts background collections (an induced
    /// <see cref="GC.Collect()"/> is always blocking). The first gen2 collection of a process can be blocking even
    /// with background collections on, so it tries a few times. A background collection is started before the
    /// window opens and ends inside it. If every attempt was blocking, background collections are off, and one
    /// more blocking collection runs inside the window.
    /// </summary>
    private sealed class Gen2Collector
    {
        private readonly object?[] _large = new object?[8];
        private int _ready;
        private int _windowOpen;
        private int _finished;
        private int _endedInWindow;

        public bool CollectionEndedInWindow => Volatile.Read(ref _endedInWindow) != 0;

        public void Run()
        {
            for (int attempt = 1; attempt <= MaxGen2Attempts; attempt++)
            {
                long background = GC.GetGCMemoryInfo(GCKind.Background).Index;
                long fullBlocking = GC.GetGCMemoryInfo(GCKind.FullBlocking).Index;
                TriggerGen2();
                if (GC.GetGCMemoryInfo(GCKind.FullBlocking).Index == fullBlocking
                    && GC.GetGCMemoryInfo(GCKind.Background).Index == background)
                {
                    // Running in the background. If it is still running once the window is open, it ends inside it.
                    SignalReadyAndWaitForWindow();
                    bool runningInWindow = GC.GetGCMemoryInfo(GCKind.Background).Index == background;
                    while (GC.GetGCMemoryInfo(GCKind.Background).Index == background)
                    {
                        Thread.Yield();
                    }

                    Volatile.Write(ref _endedInWindow, runningInWindow ? 1 : 0);
                    return;
                }
            }

            SignalReadyAndWaitForWindow();
            TriggerGen2();
            Volatile.Write(ref _endedInWindow, 1);
        }

        public void WaitUntilReady()
        {
            while (Volatile.Read(ref _ready) == 0 && Volatile.Read(ref _finished) == 0)
            {
                Thread.Yield();
            }
        }

        public void OpenWindow() => Volatile.Write(ref _windowOpen, 1);

        public void Finish()
        {
            Volatile.Write(ref _finished, 1);
        }

        private void SignalReadyAndWaitForWindow()
        {
            Volatile.Write(ref _ready, 1);
            while (Volatile.Read(ref _windowOpen) == 0)
            {
                Thread.Yield();
            }
        }

        private void TriggerGen2()
        {
            int gen2 = GC.CollectionCount(2);
            for (int i = 0; GC.CollectionCount(2) == gen2; i++)
            {
                Assert.True(i < MaxLargeAllocations, "Large-object allocations didn't trigger a gen2 collection.");
                _large[i % _large.Length] = new byte[128 * 1024];
            }
        }
    }
}
