using System;
using System.Collections.Generic;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Structural;
using Xunit;

namespace BrutalEngine.World.Tests.Ticks;

/// <summary>Shared helpers for the tick tests: ticks read per component, so assertions don't depend on column order.</summary>
internal static class TickTestSupport
{
    /// <summary>One boundary: 2³⁰, the clamp period.</summary>
    public const uint Interval = 1u << 30;

    /// <summary>The column of <typeparamref name="T"/> in <paramref name="archetype"/>'s chunks.</summary>
    public static int ColumnOf<T>(EcsWorld world, Archetype archetype)
        where T : unmanaged, IComponent
    {
        Assert.True(world.Components.TryGetId<T>(out ComponentId id));
        int typeIndex = archetype.Signature.Ids.BinarySearch(id.Index);
        Assert.True(typeIndex >= 0, $"The archetype doesn't have {typeof(T).Name}.");
        int column = archetype.Layout.ColumnOf(typeIndex);
        Assert.True(column >= 0, $"{typeof(T).Name} has no column.");
        return column;
    }

    /// <summary>The <c>Changed</c> tick of <typeparamref name="T"/> in the chunk of <paramref name="entity"/>.</summary>
    public static uint ChangedOf<T>(EcsWorld world, Entity entity)
        where T : unmanaged, IComponent
    {
        EntityRecord record = StructuralSupport.Location(world, entity);
        Archetype archetype = world.Archetypes[record.Archetype];
        return archetype.Chunks.GetChanged(record.Chunk)[ColumnOf<T>(world, archetype)];
    }

    /// <summary>The <c>Added</c> tick of <typeparamref name="T"/> in the chunk of <paramref name="entity"/>.</summary>
    public static uint AddedOf<T>(EcsWorld world, Entity entity)
        where T : unmanaged, IComponent
    {
        EntityRecord record = StructuralSupport.Location(world, entity);
        Archetype archetype = world.Archetypes[record.Archetype];
        return archetype.Chunks.GetAdded(record.Chunk)[ColumnOf<T>(world, archetype)];
    }

    /// <summary>Every chunk tick of every archetype, <c>Changed</c> then <c>Added</c> per chunk, in archetype order.</summary>
    public static uint[] AllChunkTicks(EcsWorld world)
    {
        List<uint> ticks = [];
        for (int a = 0; a < world.ArchetypeCount; a++)
        {
            ChunkTable chunks = world.Archetypes[a].Chunks;
            for (int c = 0; c < chunks.ChunkCount; c++)
            {
                ticks.AddRange(chunks.GetChanged(c).ToArray());
                ticks.AddRange(chunks.GetAdded(c).ToArray());
            }
        }

        return [.. ticks];
    }
}
