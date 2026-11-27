using System;
using System.Collections.Generic;
using System.Text;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Structural;

/// <summary>Shared checks for the structural-change tests: locations, the dense invariant and whole-world snapshots.</summary>
internal static class StructuralSupport
{
    /// <summary>
    /// Registers the test components in a fixed order, so their ids, and so every archetype's column order, are known:
    /// Position 0, Health 1, Payload 2, Marker 3 (a tag, no column).
    /// </summary>
    public static void RegisterInOrder(EcsWorld world)
    {
        Assert.Equal(0, world.Components.Register<Position>().Index);
        Assert.Equal(1, world.Components.Register<Health>().Index);
        Assert.Equal(2, world.Components.Register<Payload>().Index);
        Assert.Equal(3, world.Components.Register<Marker>().Index);
    }

    /// <summary>The entity's record: archetype, chunk and row.</summary>
    public static EntityRecord Location(EcsWorld world, Entity entity)
    {
        Assert.True(world.Entities.TryGetLocation(entity, out EntityRecord record), $"{entity} isn't alive.");
        return record;
    }

    /// <summary>The archetype the entity is stored in.</summary>
    public static Archetype ArchetypeOf(EcsWorld world, Entity entity) => world.Archetypes[Location(world, entity).Archetype];

    /// <summary>
    /// Checks the dense invariant and the entity table against the chunks: every chunk but an archetype's last is full,
    /// no chunk is empty, the row counts add up, and each row's entity id has a record that points back at that row.
    /// </summary>
    public static void AssertDense(EcsWorld world)
    {
        int rows = 0;
        for (int a = 0; a < world.ArchetypeCount; a++)
        {
            Archetype archetype = world.Archetypes[a];
            ChunkTable chunks = archetype.Chunks;
            int archetypeRows = 0;
            for (int c = 0; c < chunks.ChunkCount; c++)
            {
                int count = chunks.GetCount(c);
                Assert.InRange(count, 1, archetype.Layout.Capacity);
                if (c < chunks.ChunkCount - 1)
                {
                    Assert.Equal(archetype.Layout.Capacity, count);
                }

                for (int r = 0; r < count; r++)
                {
                    EntityRecord record = Location(world, EntityTestSupport.EntityAt(archetype, c, r));
                    Assert.Equal((a, c, r), (record.Archetype, record.Chunk, record.Row));
                }

                archetypeRows += count;
            }

            Assert.Equal(archetypeRows, archetype.RowCount);
            rows += archetypeRows;
        }

        Assert.Equal(world.EntityCount, rows);
    }

    /// <summary>
    /// A text dump of every archetype's chunks: row counts, entity ids, <c>Changed</c> and <c>Added</c> ticks, and the
    /// raw bytes of every live row's columns. Two equal dumps mean nothing in storage changed.
    /// </summary>
    public static unsafe string Dump(EcsWorld world)
    {
        StringBuilder text = new();
        text.Append("archetypes=").Append(world.ArchetypeCount).Append(" entities=").Append(world.EntityCount).AppendLine();
        for (int a = 0; a < world.ArchetypeCount; a++)
        {
            Archetype archetype = world.Archetypes[a];
            ChunkTable chunks = archetype.Chunks;
            for (int c = 0; c < chunks.ChunkCount; c++)
            {
                int count = chunks.GetCount(c);
                text.Append('A').Append(a).Append(" C").Append(c).Append(" n=").Append(count)
                    .Append(" changed=").AppendJoin(',', chunks.GetChanged(c).ToArray())
                    .Append(" added=").AppendJoin(',', chunks.GetAdded(c).ToArray()).Append(" ids=");
                for (int r = 0; r < count; r++)
                {
                    text.Append(EntityTestSupport.EntityAt(archetype, c, r)).Append(';');
                }

                byte* data = chunks.GetData(c);
                ReadOnlySpan<ComponentType> columns = archetype.Layout.Columns;
                for (int column = 0; column < columns.Length; column++)
                {
                    // Sound: the column holds Capacity values of Size bytes from its offset, and count <= Capacity.
                    var bytes = new ReadOnlySpan<byte>(data + archetype.Layout.ColumnOffsets[column], count * columns[column].Size);
                    text.Append(" col").Append(column).Append('=').Append(Convert.ToHexString(bytes));
                }

                text.AppendLine();
            }
        }

        return text.ToString();
    }

    /// <summary>The entities spawned so far in a test, with the components each one is expected to have.</summary>
    public sealed class Expected
    {
        private readonly Dictionary<Entity, Values> _values = [];

        public IEnumerable<Entity> Entities => _values.Keys;

        public Values this[Entity entity]
        {
            get => _values[entity];
            set => _values[entity] = value;
        }

        /// <summary>Checks every expected entity's components, by presence and by value, against the world.</summary>
        public void AssertWorld(EcsWorld world)
        {
            Assert.Equal(_values.Count, world.EntityCount);
            foreach ((Entity entity, Values values) in _values)
            {
                Assert.True(world.IsAlive(entity));
                Assert.Equal(values.Position is not null, world.Has<Position>(entity));
                Assert.Equal(values.Health is not null, world.Has<Health>(entity));
                Assert.Equal(values.Payload is not null, world.Has<Payload>(entity));
                Assert.Equal(values.Marker, world.Has<Marker>(entity));
                if (values.Position is { } position)
                {
                    Assert.Equal(position, world.Get<Position>(entity));
                }

                if (values.Health is { } health)
                {
                    Assert.Equal(health, world.Get<Health>(entity).Value);
                }

                if (values.Payload is { } payload)
                {
                    Assert.True(EntityComponents.Same(new Payload(payload), world.Get<Payload>(entity)), $"{entity}'s Payload differs.");
                }
            }
        }
    }

    /// <summary>One entity's expected components: null for a data component it lacks; the Payload's seed.</summary>
    public sealed record Values(Position? Position, int? Health, long? Payload, bool Marker);
}
