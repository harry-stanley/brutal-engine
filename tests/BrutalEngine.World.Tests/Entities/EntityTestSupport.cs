using System;
using System.Threading;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using Xunit;

namespace BrutalEngine.World.Tests.Entities;

/// <summary>Shared helpers for the entity API tests: allocators, worlds, row reads and running on another thread.</summary>
internal static class EntityTestSupport
{
    /// <summary>A fresh tracking allocator per test, so each test's leak check sees only its own blocks.</summary>
    public static TrackingAllocator NewTracking() => new(new HeapAllocator(), "Entities");

    /// <summary>A world over <paramref name="allocator"/> with the smallest chunks, so few rows fill a chunk.</summary>
    public static EcsWorld NewWorld(INativeAllocator allocator, int initialEntityCapacity = 16, int maxEntities = 1 << 20) =>
        new(new WorldOptions
        {
            Allocator = allocator,
            ChunkBytes = WorldOptions.SmallestChunkBytes,
            InitialEntityCapacity = initialEntityCapacity,
            MaxEntities = maxEntities,
        });

    /// <summary>The entity stored in a row, read from the chunk's entity ids.</summary>
    public static unsafe Entity EntityAt(Archetype archetype, int chunk, int row)
    {
        Assert.InRange(row, 0, archetype.Chunks.GetCount(chunk) - 1);

        // Sound: the row is a live row of the chunk (checked above), and the entity ids come first in a chunk.
        return ((Entity*)archetype.Chunks.GetData(chunk))[row];
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a new thread and waits for it, bounded by the test's cancellation token. Any
    /// exception is caught there and returned, so it can't escape the thread and take down the test host.
    /// </summary>
    public static Exception? RunOnOtherThread(Action action)
    {
        Exception? failure = null;
        using ManualResetEventSlim done = new();
        Thread thread = new(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                done.Set();
            }
        });
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken), "The other thread didn't finish.");
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "The other thread didn't exit.");
        return failure;
    }
}
