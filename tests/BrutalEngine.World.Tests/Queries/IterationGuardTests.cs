using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// The iteration guard (docs/design/world.md, section 5, "Iteration guard"): every structural entry point throws while
/// a chunk enumerator of the world is live, before it looks at its arguments, and works again once every enumerator is
/// disposed; nested enumeration is allowed.
/// </summary>
public sealed class IterationGuardTests
{
    public enum StructuralOp
    {
        Spawn,
        Spawn1,
        Spawn2,
        Spawn3,
        Spawn4,
        Despawn,
        DespawnNull,
        Add,
        SetPresent,
        SetAbsent,
        RemovePresent,
        RemoveAbsent,
        RefAdd,
        RefDespawn,
    }

    public static TheoryData<StructuralOp> Ops => [.. Enum.GetValues<StructuralOp>()];

    [Theory]
    [MemberData(nameof(Ops))]
    public void StructuralChangeDuringForeach_Throws(StructuralOp op)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity entity = world.Spawn(new Position(1, 2), new Health(3));
            Entity other = world.Spawn(new Position(4, 5), new Health(6));
            using Query query = world.Query().Read<Position>().Build();
            string snapshot = Snapshot(world, entity, other);
            int chunks = 0;
            foreach (ChunkView chunk in query)
            {
                chunks++;
                InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => Apply(world, op, entity));
                Assert.Contains("structural change", thrown.Message, StringComparison.Ordinal);
                Assert.Contains(ApiName(op), thrown.Message, StringComparison.Ordinal);

                // Checked before anything changes, the arguments included.
                Assert.Equal(snapshot, Snapshot(world, entity, other));
                Assert.Equal(2, chunk.Count);
            }

            Assert.Equal(1, chunks);
            Assert.Equal(snapshot, Snapshot(world, entity, other));

            // Once the loop has ended, the same call works (Entity.Null now fails its own argument check).
            if (op == StructuralOp.DespawnNull)
            {
                Assert.Throws<ArgumentException>(() => Apply(world, op, entity));
            }
            else
            {
                Apply(world, op, entity);
            }
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Nested_Allowed()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity[] entities = EnumerationSupport.SpawnChunks(world, chunks: 2, lastRows: 1);
            _ = world.Spawn(new Position(1, 1));
            using Query outer = world.Query().Read<Payload>().Build();
            using Query inner = world.Query().Read<Position>().Build();
            int outerChunks = 0;
            int innerChunks = 0;
            int sameChunks = 0;
            foreach (ChunkView chunk in outer)
            {
                outerChunks++;
                foreach (ChunkView nested in inner)
                {
                    innerChunks += nested.Count;
                    Assert.Throws<InvalidOperationException>(() => world.Spawn());
                }

                // The same query nested in itself, and the guard still holds after an inner loop ends.
                foreach (ChunkView nested in outer)
                {
                    sameChunks++;
                }

                Assert.Throws<InvalidOperationException>(() => world.Despawn(entities[0]));
                Assert.Equal(chunk.Count, chunk.Read<Payload>().Length);
            }

            Assert.Equal(2, outerChunks);
            Assert.Equal(2, innerChunks);
            Assert.Equal(4, sameChunks);
            Assert.True(inner.HasRun);
            world.Despawn(entities[0]);
            Assert.Equal(0, world.IterationGuard.LiveCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void ManualEnumerator_GuardsUntilDispose()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = world.Spawn(new Position(1, 1));
            using Query query = world.Query().Read<Position>().Build();

            // The guard starts with GetEnumerator, not the first MoveNext, and ends with Dispose, not the last MoveNext.
            ChunkEnumerator enumerator = query.GetEnumerator();
            Assert.Throws<InvalidOperationException>(() => world.Spawn());
            Assert.True(enumerator.MoveNext());
            Assert.False(enumerator.MoveNext());
            Assert.False(enumerator.MoveNext());
            Assert.Throws<InvalidOperationException>(() => world.Spawn());
            enumerator.Dispose();
            enumerator.Dispose();
            _ = world.Spawn();

            // A copy disposed after the original has no enumeration left to end.
            ChunkEnumerator original = query.GetEnumerator();
            ChunkEnumerator copy = original;
            original.Dispose();
            InvalidOperationException copied = Assert.IsType<InvalidOperationException>(DisposeOf(copy));
            Assert.Contains("copied", copied.Message, StringComparison.Ordinal);
            Assert.Equal(0, world.IterationGuard.LiveCount);
            _ = world.Spawn();

            // A default enumerator yields nothing and guards nothing.
            ChunkEnumerator none = default;
            Assert.False(none.MoveNext());
            none.Dispose();
            Assert.Equal(0, world.IterationGuard.LiveCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void QueryDisposedDuringLoop_ThrowsAndReleasesGuard()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = EnumerationSupport.SpawnChunks(world, chunks: 2, lastRows: 1);
            Query query = world.Query().Read<Payload>().Build();
            Assert.Throws<ObjectDisposedException>(() =>
            {
                foreach (ChunkView chunk in query)
                {
                    query.Dispose();
                    Assert.IsType<ObjectDisposedException>(ReadOf(chunk));
                }
            });
            Assert.Equal(0, world.IterationGuard.LiveCount);
            _ = world.Spawn();
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
                Assert.IsType<ObjectDisposedException>(ReadOf(chunk));
            }
        });
        Assert.Throws<ObjectDisposedException>(() => query.GetEnumerator().Dispose());
        tracking.ThrowIfLeaks();
    }

    private static void Apply(EcsWorld world, StructuralOp op, Entity entity)
    {
        switch (op)
        {
            case StructuralOp.Spawn:
                _ = world.Spawn();
                break;
            case StructuralOp.Spawn1:
                _ = world.Spawn(new Payload(1));
                break;
            case StructuralOp.Spawn2:
                _ = world.Spawn(new Payload(1), new Marker());
                break;
            case StructuralOp.Spawn3:
                _ = world.Spawn(new Payload(1), new Marker(), new Position(1, 1));
                break;
            case StructuralOp.Spawn4:
                _ = world.Spawn(new Payload(1), new Marker(), new Position(1, 1), new Health(1));
                break;
            case StructuralOp.Despawn:
                world.Despawn(entity);
                break;
            case StructuralOp.DespawnNull:
                world.Despawn(Entity.Null);
                break;
            case StructuralOp.Add:
                world.Add(entity, new Marker());
                break;
            case StructuralOp.SetPresent:
                world.Set(entity, new Health(9));
                break;
            case StructuralOp.SetAbsent:
                world.Set(entity, new Payload(9));
                break;
            case StructuralOp.RemovePresent:
                _ = world.Remove<Health>(entity);
                break;
            case StructuralOp.RemoveAbsent:
                _ = world.Remove<Payload>(entity);
                break;
            case StructuralOp.RefAdd:
                world.Ref(entity).Add(new Marker());
                break;
            default:
                world.Ref(entity).Despawn();
                break;
        }
    }

    private static string ApiName(StructuralOp op) => op switch
    {
        StructuralOp.Spawn or StructuralOp.Spawn1 or StructuralOp.Spawn2 or StructuralOp.Spawn3 or StructuralOp.Spawn4 => "EcsWorld.Spawn",
        StructuralOp.Despawn or StructuralOp.DespawnNull or StructuralOp.RefDespawn => "EcsWorld.Despawn",
        StructuralOp.Add or StructuralOp.RefAdd => "EcsWorld.Add",
        StructuralOp.SetPresent or StructuralOp.SetAbsent => "EcsWorld.Set",
        _ => "EcsWorld.Remove",
    };

    // Everything an op could change: counts, both entities' components and values, and every tick of their archetype.
    private static string Snapshot(EcsWorld world, Entity entity, Entity other) =>
        string.Join(
            ",",
            world.EntityCount,
            world.ArchetypeCount,
            world.ChunkCount,
            world.IsAlive(entity),
            world.Has<Marker>(entity),
            world.Has<Payload>(entity),
            world.Get<Health>(entity).Value,
            world.Get<Position>(other).X,
            string.Join("/", EnumerationSupport.Ticks(EnumerationSupport.ArchetypeOf(world, entity))));

    private static Exception? DisposeOf(ChunkEnumerator enumerator)
    {
        try
        {
            enumerator.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception? ReadOf(ChunkView chunk)
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
