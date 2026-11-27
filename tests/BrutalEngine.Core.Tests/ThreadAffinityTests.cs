using System;
using System.Threading;
using Xunit;

namespace BrutalEngine.Core.Tests;

public sealed class ThreadAffinityTests
{
    private const string ApiName = "Window.SetTitle";

    [Fact]
    public void CurrentThread_OnSameThread_IsCurrentThreadTrue()
    {
        var affinity = ThreadAffinity.CurrentThread();

        Assert.True(affinity.IsCurrentThread);
        Assert.Equal(Environment.CurrentManagedThreadId, affinity.ThreadId);
    }

    [Fact]
    public void IsCurrentThread_OnOtherThread_False()
    {
        var affinity = ThreadAffinity.CurrentThread();
        bool isCurrentThread = true;

        OtherThreadRun run = RunOnOtherThread(() => isCurrentThread = affinity.IsCurrentThread);

        Assert.Null(run.Failure);
        Assert.NotEqual(affinity.ThreadId, run.ThreadId);
        Assert.False(isCurrentThread);
    }

    [Fact]
    public void CurrentThread_OnOtherThread_CapturesThatThreadsId()
    {
        ThreadAffinity affinity = default;

        OtherThreadRun run = RunOnOtherThread(() => affinity = ThreadAffinity.CurrentThread());

        Assert.Null(run.Failure);
        Assert.Equal(run.ThreadId, affinity.ThreadId);
    }

    [Fact]
    public void IsCurrentThread_ForOtherThreadsAffinity_IsFalse()
    {
        ThreadAffinity affinity = default;

        OtherThreadRun run = RunOnOtherThread(() => affinity = ThreadAffinity.CurrentThread());

        // The other thread has exited, so its id may be reused, but never by this thread: it was alive throughout.
        Assert.Null(run.Failure);
        Assert.False(affinity.IsCurrentThread);
    }

    [Fact]
    public void ThrowIfNotCurrentThread_OnOtherThread_ThrowsWithApiNameAndIds()
    {
        var affinity = ThreadAffinity.CurrentThread();

        OtherThreadRun run = RunOnOtherThread(() => affinity.ThrowIfNotCurrentThread(ApiName));

        WrongThreadException exception = Assert.IsType<WrongThreadException>(run.Failure);
        Assert.Equal(ApiName, exception.ApiName);
        Assert.Equal(affinity.ThreadId, exception.ExpectedThreadId);
        Assert.Equal(run.ThreadId, exception.ActualThreadId);
        Assert.NotEqual(exception.ExpectedThreadId, exception.ActualThreadId);
    }

    [Fact]
    public void ThrowIfNotCurrentThread_OnOwnerThread_DoesNotThrow()
    {
        var affinity = ThreadAffinity.CurrentThread();

        Exception? exception = Record.Exception(() => affinity.ThrowIfNotCurrentThread(ApiName));

        Assert.Null(exception);
    }

    [Fact]
    public void ThrowIfNotCurrentThread_NullApiName_ThrowsArgumentNull()
    {
        var affinity = ThreadAffinity.CurrentThread();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => affinity.ThrowIfNotCurrentThread(null!));

        Assert.Equal("apiName", exception.ParamName);
    }

    [Fact]
    public void Default_Checks_ThrowInvalidOperation()
    {
        ThreadAffinity affinity = default;

        // Assert.Throws checks the exact type, so a WrongThreadException (a subclass) fails here.
        Assert.Throws<InvalidOperationException>(() => affinity.IsCurrentThread);
        Assert.Throws<InvalidOperationException>(() => affinity.ThrowIfNotCurrentThread(ApiName));
    }

    [Fact]
    public void Default_ThreadId_IsZero()
    {
        ThreadAffinity affinity = default;

        Assert.Equal(0, affinity.ThreadId);
    }

    [Fact]
    public void ThrowIfNotCurrentThread_OnOwnerThread_DoesNotAllocate()
    {
        var affinity = ThreadAffinity.CurrentThread();

        // Warm up so any one-time JIT or tiering work happens before measuring.
        affinity.ThrowIfNotCurrentThread(ApiName);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            affinity.ThrowIfNotCurrentThread(ApiName);
        }

        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a new thread and waits for it. Any exception, a failed assertion included,
    /// is caught there and returned, so it can't escape the thread and take down the test host.
    /// </summary>
    private static OtherThreadRun RunOnOtherThread(Action action)
    {
        int threadId = 0;
        Exception? failure = null;
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
        return new OtherThreadRun(threadId, failure);
    }

    private readonly record struct OtherThreadRun(int ThreadId, Exception? Failure);
}
