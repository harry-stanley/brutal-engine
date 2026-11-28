using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// <see cref="ChunkView"/> (docs/design/world.md, section 5, "Declared access is enforced"): span access only to the
/// columns a query declares, <c>Write</c> stamping its column when called, <c>Read</c> never stamping, and absent
/// optional columns.
/// </summary>
public sealed class ChunkViewTests
{
    public enum ViewOp
    {
        Read,
        Write,
        Changed,
        Added,
    }

    // The expected outcome of each view member per term on Position, written from the design's access rules: Read,
    // Changed and Added need any data term; Write needs Write or OptionalWrite; With and an undeclared type grant none.
    public static TheoryData<string, ViewOp, bool> AccessRows => new()
    {
        { "Read", ViewOp.Read, true }, { "Read", ViewOp.Write, false }, { "Read", ViewOp.Changed, true }, { "Read", ViewOp.Added, true },
        { "Write", ViewOp.Read, true }, { "Write", ViewOp.Write, true }, { "Write", ViewOp.Changed, true }, { "Write", ViewOp.Added, true },
        { "With", ViewOp.Read, false }, { "With", ViewOp.Write, false }, { "With", ViewOp.Changed, false }, { "With", ViewOp.Added, false },
        { "OptionalRead", ViewOp.Read, true }, { "OptionalRead", ViewOp.Write, false }, { "OptionalRead", ViewOp.Changed, true }, { "OptionalRead", ViewOp.Added, true },
        { "OptionalWrite", ViewOp.Read, true }, { "OptionalWrite", ViewOp.Write, true }, { "OptionalWrite", ViewOp.Changed, true }, { "OptionalWrite", ViewOp.Added, true },
        { "Changed", ViewOp.Read, true }, { "Changed", ViewOp.Write, false }, { "Changed", ViewOp.Changed, true }, { "Changed", ViewOp.Added, true },
        { "Added", ViewOp.Read, true }, { "Added", ViewOp.Write, false }, { "Added", ViewOp.Changed, true }, { "Added", ViewOp.Added, true },
        { "WriteChanged", ViewOp.Read, true }, { "WriteChanged", ViewOp.Write, true }, { "WithAdded", ViewOp.Read, true }, { "WithAdded", ViewOp.Write, false },
        { "Undeclared", ViewOp.Read, false }, { "Undeclared", ViewOp.Write, false }, { "Undeclared", ViewOp.Changed, false }, { "Undeclared", ViewOp.Added, false },
    };

    [Theory]
    [MemberData(nameof(AccessRows))]
    public void Access_DeclaredOnly(string positionTerm, ViewOp op, bool allowed)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity entity = world.Spawn(new Position(3, 4), new Health(5));
            uint[] before = EnumerationSupport.Ticks(EnumerationSupport.ArchetypeOf(world, entity));
            world.AdvanceChangeTick();
            using Query query = WithPositionTerm(world.Query().Read<Health>(), positionTerm).Build();
            int chunks = 0;
            foreach (ChunkView chunk in query)
            {
                chunks++;
                Exception? thrown = Run(chunk, op, out float firstX);
                if (allowed)
                {
                    Assert.Null(thrown);
                    Assert.True(op is not (ViewOp.Read or ViewOp.Write) || firstX == 3, "the span isn't the Position column");
                }
                else
                {
                    InvalidOperationException rejected = Assert.IsType<InvalidOperationException>(thrown);
                    Assert.Contains("doesn't declare " + (op == ViewOp.Write ? "write" : "read") + " access", rejected.Message, StringComparison.Ordinal);
                    Assert.Contains(typeof(Position).FullName!, rejected.Message, StringComparison.Ordinal);
                }
            }

            Assert.Equal(1, chunks);

            // A rejected Write stamps nothing.
            if (!allowed)
            {
                Assert.Equal(before, EnumerationSupport.Ticks(EnumerationSupport.ArchetypeOf(world, entity)));
            }
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Access_WithoutGrantsNone()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = world.Spawn(new Position(1, 2));
            using Query query = world.Query().Read<Position>().Without<Health>().Build();
            foreach (ChunkView chunk in query)
            {
                InvalidOperationException read = Assert.IsType<InvalidOperationException>(RunHealth(chunk, ViewOp.Read));
                Assert.Contains("doesn't declare read access", read.Message, StringComparison.Ordinal);
                InvalidOperationException write = Assert.IsType<InvalidOperationException>(RunHealth(chunk, ViewOp.Write));
                Assert.Contains("doesn't declare write access", write.Message, StringComparison.Ordinal);
            }
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Access_UnregisteredType_RejectedAndHasNeedsNoTerm()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = world.Spawn(new Position(1, 2), new Marker());
            using Query query = world.Query().Read<Position>().Build();
            foreach (ChunkView chunk in query)
            {
                Assert.IsType<InvalidOperationException>(ReadUnused(chunk));

                // Has reads only the mask: no term needed, and an unregistered type is absent.
                Assert.True(chunk.Has<Position>());
                Assert.True(chunk.Has<Marker>());
                Assert.False(chunk.Has<Health>());
                Assert.False(chunk.Has<Unused>());
            }

            Assert.False(world.Components.TryGetId<Unused>(out _));
        }

        tracking.ThrowIfLeaks();
    }

    // Payload is column 0 of (Payload, Health); written second, its data term (1) differs from its column.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Write_StampsWithoutRows(bool writeTermFirst)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity[] entities = EnumerationSupport.SpawnChunks(world, chunks: 3, lastRows: 2);
            Archetype archetype = EnumerationSupport.ArchetypeOf(world, entities[0]);
            int payload = EnumerationSupport.ColumnOf<Payload>(world, archetype);
            int health = EnumerationSupport.ColumnOf<Health>(world, archetype);
            Assert.Equal(0, payload);
            world.SetChangeTickForTesting(100);
            using Query query = writeTermFirst
                ? world.Query().Write<Payload>().Read<Health>().Build()
                : world.Query().Read<Health>().Write<Payload>().Build();
            int index = 0;
            foreach (ChunkView chunk in query)
            {
                // Only chunk 1 is written, and no row of it changes.
                if (index++ == 1)
                {
                    Span<Payload> values = chunk.Write<Payload>();
                    Assert.Equal(chunk.Count, values.Length);
                }
            }

            Assert.Equal(100u, archetype.Chunks.GetChanged(1)[payload]);
            Assert.Equal(1u, archetype.Chunks.GetChanged(1)[health]);
            Assert.Equal(1u, archetype.Chunks.GetAdded(1)[payload]);
            Assert.Equal(1u, archetype.Chunks.GetChanged(0)[payload]);
            Assert.Equal(1u, archetype.Chunks.GetChanged(2)[payload]);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Read_NeverStamps()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity[] entities = EnumerationSupport.SpawnChunks(world, chunks: 2, lastRows: 3);
            Archetype archetype = EnumerationSupport.ArchetypeOf(world, entities[0]);
            uint[] before = EnumerationSupport.Ticks(archetype);
            world.SetChangeTickForTesting(50);

            // Health is declared Write, so reading it through the write term mustn't stamp either.
            using Query query = world.Query().Read<Payload>().Write<Health>().Changed<Payload>().Build();
            long checksum = 0;
            foreach (ChunkView chunk in query)
            {
                checksum += chunk.Read<Payload>()[0].Values[0] + chunk.Read<Health>()[chunk.Count - 1].Value + chunk.Count + chunk.Entities.Length;
                checksum += (chunk.Has<Payload>() ? 1 : 0) + (chunk.Changed<Payload>() ? 1 : 0) + (chunk.Added<Health>() ? 1 : 0);
            }

            Assert.Equal(before, EnumerationSupport.Ticks(archetype));
            Assert.NotEqual(0, checksum);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void AbsentOptional_HasFalse_ReadWriteThrow()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            Entity without = world.Spawn(new Position(1, 1));
            Entity with = world.Spawn(new Position(2, 2), new Health(7));
            uint[] before = EnumerationSupport.Ticks(EnumerationSupport.ArchetypeOf(world, without));
            using Query query = world.Query().Read<Position>().OptionalWrite<Health>().Build();
            int chunks = 0;
            foreach (ChunkView chunk in query)
            {
                chunks++;
                if (chunk.Entities[0] == without)
                {
                    Assert.False(chunk.Has<Health>());
                    InvalidOperationException read = Assert.IsType<InvalidOperationException>(RunHealth(chunk, ViewOp.Read));
                    Assert.Contains("has no column", read.Message, StringComparison.Ordinal);
                    Assert.IsType<InvalidOperationException>(RunHealth(chunk, ViewOp.Write));

                    // Before the first run everything that exists is new, but an absent component doesn't exist here.
                    Assert.False(chunk.Changed<Health>());
                    Assert.False(chunk.Added<Health>());
                }
                else
                {
                    Assert.Equal(with, chunk.Entities[0]);
                    Assert.True(chunk.Has<Health>());
                    Assert.Equal(7, chunk.Read<Health>()[0].Value);
                    chunk.Write<Health>()[0].Value = 8;
                    Assert.True(chunk.Changed<Health>());
                }
            }

            Assert.Equal(2, chunks);
            Assert.Equal(8, world.Get<Health>(with).Value);
            Assert.Equal(before, EnumerationSupport.Ticks(EnumerationSupport.ArchetypeOf(world, without)));
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Tag_HasNoColumn()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            _ = world.Spawn(new Position(1, 1), new Marker());
            using Query query = world.Query().Read<Marker>().Build();
            foreach (ChunkView chunk in query)
            {
                Assert.True(chunk.Has<Marker>());
                InvalidOperationException read = Assert.IsType<InvalidOperationException>(ReadMarker(chunk));
                Assert.Contains("has no column", read.Message, StringComparison.Ordinal);
            }
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void DefaultView_Throws()
    {
        ChunkView view = default;
        foreach (ViewOp op in Enum.GetValues<ViewOp>())
        {
            InvalidOperationException thrown = Assert.IsType<InvalidOperationException>(Run(view, op, out _));
            Assert.Contains("default", thrown.Message, StringComparison.Ordinal);
        }

        Assert.IsType<InvalidOperationException>(CountOf(default));
    }

    private static QueryBuilder WithPositionTerm(QueryBuilder builder, string term) => term switch
    {
        "Read" => builder.Read<Position>(),
        "Write" => builder.Write<Position>(),
        "With" => builder.With<Position>(),
        "OptionalRead" => builder.OptionalRead<Position>(),
        "OptionalWrite" => builder.OptionalWrite<Position>(),
        "Changed" => builder.Changed<Position>(),
        "Added" => builder.Added<Position>(),
        "WriteChanged" => builder.Write<Position>().Changed<Position>(),
        "WithAdded" => builder.With<Position>().Added<Position>(),
        "Undeclared" => builder,
        _ => throw new ArgumentOutOfRangeException(nameof(term), term, null),
    };

    // Runs one view member on Position, catching what it throws: a ref struct can't go into a lambda.
    private static Exception? Run(ChunkView chunk, ViewOp op, out float firstX)
    {
        firstX = float.NaN;
        try
        {
            switch (op)
            {
                case ViewOp.Read:
                    firstX = chunk.Read<Position>()[0].X;
                    break;
                case ViewOp.Write:
                    firstX = chunk.Write<Position>()[0].X;
                    break;
                case ViewOp.Changed:
                    _ = chunk.Changed<Position>();
                    break;
                default:
                    _ = chunk.Added<Position>();
                    break;
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception? RunHealth(ChunkView chunk, ViewOp op)
    {
        try
        {
            _ = op == ViewOp.Read ? chunk.Read<Health>().Length : chunk.Write<Health>().Length;
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception? ReadUnused(ChunkView chunk)
    {
        try
        {
            _ = chunk.Read<Unused>();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception? ReadMarker(ChunkView chunk)
    {
        try
        {
            _ = chunk.Read<Marker>();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static Exception? CountOf(ChunkView chunk)
    {
        try
        {
            _ = chunk.Count + chunk.Entities.Length + (chunk.Has<Position>() ? 1 : 0);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
