using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// <see cref="ChunkEnumerator"/>'s documented contract (docs/design/world.md, section 5): <c>Current</c> before the first
/// move and after the end, <c>Dispose</c> ending the enumeration, a default enumerator, and a query or world disposed
/// during a loop.
/// </summary>
public sealed class ChunkEnumeratorTests
{
    [Fact]
    public void Current_IsDefaultBeforeTheFirstMoveAndAfterTheEnd()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity entity = world.Spawn(new Position(1, 1));
            using Query query = world.Query().Read<Position>().Build();
            ChunkEnumerator enumerator = query.GetEnumerator();
            try
            {
                Assert.IsType<InvalidOperationException>(CountOf(enumerator.Current));
                Assert.True(enumerator.MoveNext());
                Assert.Equal(entity, enumerator.Current.Entities[0]);
                Assert.False(enumerator.MoveNext());
                Assert.IsType<InvalidOperationException>(CountOf(enumerator.Current));
            }
            finally
            {
                enumerator.Dispose();
            }
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Dispose_EndsTheEnumeration()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = EnumerationSupport.SpawnChunks(world, chunks: 2, lastRows: 1);
            using Query query = world.Query().Read<Payload>().Build();
            ChunkEnumerator enumerator = query.GetEnumerator();
            Assert.True(enumerator.MoveNext());
            enumerator.Dispose();

            // A second chunk is left, but a disposed enumerator yields nothing more; Dispose is idempotent.
            Assert.False(enumerator.MoveNext());
            Assert.IsType<InvalidOperationException>(CountOf(enumerator.Current));
            enumerator.Dispose();
            Assert.False(enumerator.MoveNext());
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Default_YieldsNothing()
    {
        ChunkEnumerator enumerator = default;
        Assert.False(enumerator.MoveNext());
        Assert.IsType<InvalidOperationException>(CountOf(enumerator.Current));
        enumerator.Dispose();
        Assert.False(enumerator.MoveNext());
    }

    [Fact]
    public void QueryDisposedDuringLoop_Throws()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = EnumerationSupport.SpawnChunks(world, chunks: 2, lastRows: 1);
            Query query = world.Query().Read<Payload>().Build();
            int chunks = 0;
            Assert.Throws<ObjectDisposedException>(() =>
            {
                foreach (ChunkView chunk in query)
                {
                    chunks++;
                    query.Dispose();
                    Assert.IsType<ObjectDisposedException>(CountOf(chunk));
                }
            });
            Assert.Equal(1, chunks);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void WorldDisposedDuringLoop_ThrowsWithoutLeaks()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        EcsWorld world = EntityTestSupport.NewWorld(tracking);
        _ = world.Spawn(new Position(1, 1));
        Query query = world.Query().Read<Position>().Build();
        Assert.Throws<ObjectDisposedException>(() =>
        {
            foreach (ChunkView chunk in query)
            {
                world.Dispose();
                Assert.IsType<ObjectDisposedException>(CountOf(chunk));
            }
        });
        Assert.Throws<ObjectDisposedException>(() => query.GetEnumerator().Dispose());
        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void ViewOfARemovedChunk_ThrowsArgumentOutOfRange()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity[] entities = EnumerationSupport.SpawnChunks(world, chunks: 2, lastRows: 1);
            using Query query = world.Query().Read<Payload>().Build();
            ChunkView kept = default;
            foreach (ChunkView chunk in query)
            {
                kept = chunk;
            }

            // Misuse: the kept view is of chunk 1, which the despawn of its only row removes.
            world.Despawn(entities[^1]);
            Assert.IsType<ArgumentOutOfRangeException>(CountOf(kept));
        }

        tracking.ThrowIfLeaks();
    }

    // Reads a view member, returning what it threw: a ref struct can't go into a lambda.
    private static Exception? CountOf(ChunkView chunk)
    {
        try
        {
            _ = chunk.Count;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
