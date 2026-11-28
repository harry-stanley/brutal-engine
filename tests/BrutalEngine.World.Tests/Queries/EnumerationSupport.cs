using System;
using System.Collections.Generic;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// Shared helpers for the enumeration tests: multi-chunk fixtures, where an entity's row is, and the chunks a loop
/// yields, found through each chunk's first entity.
/// </summary>
internal static class EnumerationSupport
{
    /// <summary>
    /// Spawns (Payload, Health) rows into a world with the smallest chunks until their archetype has
    /// <paramref name="chunks"/> chunks and its last one holds <paramref name="lastRows"/> rows.
    /// </summary>
    public static Entity[] SpawnChunks(EcsWorld world, int chunks, int lastRows)
    {
        List<Entity> entities = [world.Spawn(new Payload(0), new Health(0))];
        Archetype archetype = ArchetypeOf(world, entities[0]);
        int total = ((chunks - 1) * archetype.Layout.Capacity) + lastRows;
        while (entities.Count < total)
        {
            entities.Add(world.Spawn(new Payload(entities.Count), new Health(entities.Count)));
        }

        Assert.Equal(chunks, archetype.Chunks.ChunkCount);
        return [.. entities];
    }

    /// <summary>The archetype an alive entity is stored in.</summary>
    public static Archetype ArchetypeOf(EcsWorld world, Entity entity) => world.Archetypes[world.Entities.Validate(entity).Archetype];

    /// <summary>The chunk index of an alive entity's row in its archetype.</summary>
    public static int ChunkOf(EcsWorld world, Entity entity) => world.Entities.Validate(entity).Chunk;

    /// <summary>The column of <typeparamref name="T"/> in <paramref name="archetype"/>'s layout.</summary>
    public static int ColumnOf<T>(EcsWorld world, Archetype archetype)
        where T : unmanaged, IComponent
    {
        int typeIndex = archetype.Signature.Ids.BinarySearch(world.Components.Register<T>().Index);
        Assert.True(typeIndex >= 0, "the archetype lacks the component");
        return archetype.Layout.ColumnOf(typeIndex);
    }

    /// <summary>Enumerates <paramref name="query"/> to completion and returns each yielded chunk's (archetype, chunk) index.</summary>
    public static List<(int Archetype, int Chunk)> Yielded(EcsWorld world, Query query)
    {
        List<(int, int)> yielded = [];
        foreach (ChunkView chunk in query)
        {
            EntityRecord record = world.Entities.Validate(chunk.Entities[0]);
            yielded.Add((record.Archetype, record.Chunk));
        }

        return yielded;
    }

    /// <summary>Enumerates <paramref name="query"/> to completion and returns the chunk index of each yielded chunk.</summary>
    public static int[] YieldedChunks(EcsWorld world, Query query)
    {
        List<(int Archetype, int Chunk)> yielded = Yielded(world, query);
        int[] chunks = new int[yielded.Count];
        for (int i = 0; i < chunks.Length; i++)
        {
            chunks[i] = yielded[i].Chunk;
        }

        return chunks;
    }

    /// <summary>A snapshot of every Changed and Added tick of an archetype's chunks.</summary>
    public static uint[] Ticks(Archetype archetype)
    {
        List<uint> ticks = [];
        for (int chunk = 0; chunk < archetype.Chunks.ChunkCount; chunk++)
        {
            ticks.AddRange(archetype.Chunks.GetChanged(chunk).ToArray());
            ticks.AddRange(archetype.Chunks.GetAdded(chunk).ToArray());
        }

        return [.. ticks];
    }

    /// <summary>Runs <paramref name="action"/> and returns what it threw, or <see langword="null"/>.</summary>
    public static Exception? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
