using System;
using System.Runtime.CompilerServices;
using System.Threading;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using Xunit;

namespace BrutalEngine.World.Tests;

public sealed class EcsWorldTests
{
    [Fact]
    public void Constructor_NullOptions_ThrowsArgumentNull()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => new EcsWorld(null!));

        Assert.Equal("options", exception.ParamName);
    }

    [Fact]
    public void Name_IsOptionsName()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking, Name = "Play" }))
        {
            Assert.Equal("Play", world.Name);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void GivenAllocator_NotDisposed()
    {
        TrackingAllocator tracking = new(new HeapAllocator(), "World");
        using DisposeCountingAllocator allocator = new(tracking);
        EcsWorld world = new(new WorldOptions { Allocator = allocator, InitialEntityCapacity = 2 });

        Assert.Same(allocator, world.Allocator);

        // Use the entity table past its initial capacity, so the world has grown blocks and freed slots to release.
        for (int i = 0; i < 5; i++)
        {
            _ = world.Entities.Allocate();
            world.Entities.Place(i, archetype: 0, chunk: 0, row: i);
        }

        world.Entities.Free(1);
        world.Entities.Free(3);
        Assert.NotEqual(0, tracking.LiveAllocationCount);

        world.Dispose();

        Assert.Equal(0, allocator.DisposeCount);
        AssertStillAllocates(allocator);
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void NullAllocator_UsesHeapAllocator()
    {
        using EcsWorld world = new(new WorldOptions());

        Assert.IsType<HeapAllocator>(world.Allocator);
    }

    [Fact]
    public void Dispose_Twice_DoesNotThrow()
    {
        TrackingAllocator tracking = NewTracking();
        EcsWorld world = new(new WorldOptions { Allocator = tracking });
        world.Dispose();

        Exception? exception = Record.Exception(world.Dispose);

        Assert.Null(exception);

        // The second Dispose freed nothing twice (the tracking allocator throws on a double free).
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AfterDispose_Throws()
    {
        TrackingAllocator tracking = NewTracking();
        EcsWorld world = new(new WorldOptions { Allocator = tracking });
        world.Dispose();

        ObjectDisposedException exception = Assert.Throws<ObjectDisposedException>(() => world.Name);

        Assert.Equal(typeof(EcsWorld).FullName, exception.ObjectName);
        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData("EcsWorld.Name")]
    [InlineData("EcsWorld.Dispose")]
    public void OtherThread_Throws(string apiName)
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = new(new WorldOptions { Allocator = tracking }))
        {
            Action<EcsWorld> call = apiName switch
            {
                "EcsWorld.Name" => w => _ = w.Name,
                "EcsWorld.Dispose" => w => w.Dispose(),
                _ => throw new ArgumentOutOfRangeException(nameof(apiName), apiName, "Unknown member."),
            };

            (Exception? failure, int threadId) = RunOnOtherThread(() => call(world));

            WrongThreadException exception = Assert.IsType<WrongThreadException>(failure);
            Assert.Equal(apiName, exception.ApiName);
            Assert.Equal(Environment.CurrentManagedThreadId, exception.ExpectedThreadId);
            Assert.Equal(threadId, exception.ActualThreadId);

            // The rejected call changed nothing: the world is still alive on its owner thread.
            Assert.Equal("World", world.Name);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Constructor_OnOtherThread_MakesThatThreadTheOwner()
    {
        TrackingAllocator tracking = NewTracking();
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        using ManualResetEventSlim created = new();
        using ManualResetEventSlim ownerChecked = new();
        EcsWorld? world = null;
        Exception? failure = null;
        int threadId = 0;

        // The creating thread stays alive until the check below is done, so it can dispose the world it owns.
        Thread thread = new(() =>
        {
            threadId = Environment.CurrentManagedThreadId;
            try
            {
                world = new EcsWorld(new WorldOptions { Allocator = tracking });
                created.Set();
                ownerChecked.Wait(cancellation);
                world.Dispose();
            }
            catch (Exception ex)
            {
                // Caught here, so it can't escape the thread and take down the test host.
                failure = ex;
            }
            finally
            {
                created.Set();
            }
        });
        thread.Start();
        created.Wait(cancellation);
        Exception? fromThisThread = Record.Exception(() => world?.Name);
        ownerChecked.Set();
        thread.Join();

        Assert.Null(failure);
        Assert.NotNull(world);
        WrongThreadException exception = Assert.IsType<WrongThreadException>(fromThisThread);
        Assert.Equal(threadId, exception.ExpectedThreadId);
        tracking.ThrowIfLeaks();
    }

    /// <summary>A fresh tracking allocator per test, so each test's leak check sees only its own blocks.</summary>
    private static TrackingAllocator NewTracking() => new(new HeapAllocator(), "World");

    private static unsafe void AssertStillAllocates(INativeAllocator allocator)
    {
        void* block = allocator.Allocate(64, 16);
        Assert.True(block != null);
        allocator.Free(block);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a new thread and waits for it. Any exception is caught there and returned,
    /// so it can't escape the thread and take down the test host.
    /// </summary>
    private static (Exception? Failure, int ThreadId) RunOnOtherThread(Action action)
    {
        Exception? failure = null;
        int threadId = 0;
        Thread thread = new(() =>
        {
            threadId = Environment.CurrentManagedThreadId;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join();
        return (failure, threadId);
    }

    /// <summary>An allocator that is also disposable, and counts how often it is disposed.</summary>
    private sealed unsafe class DisposeCountingAllocator(INativeAllocator inner) : INativeAllocator, IDisposable
    {
        public int DisposeCount { get; private set; }

        public void* Allocate(nuint byteCount, nuint alignment, [CallerFilePath] string callerFile = "", [CallerLineNumber] int callerLine = 0) =>
            inner.Allocate(byteCount, alignment, callerFile, callerLine);

        public void Free(void* memory) => inner.Free(memory);

        public void Dispose() => DisposeCount++;
    }
}
