using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using Xunit;

namespace BrutalEngine.World.Tests.Structural;

/// <summary>
/// The same seeded sequence of direct calls gives a byte-identical world: every entity id, row, change tick and
/// component byte (docs/design/world.md, section 4, "Determinism", and section 12). The world is hashed at every
/// change-tick boundary as well as at the end, so stamps that a later move overwrites are compared too.
/// </summary>
public sealed class DeterminismTests
{
    private const int Steps = 3000;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;

    [Fact]
    public void SeededScenario_SameHashTwice()
    {
        (ulong first, int firstChunks) = RunScenario(seed: 1234);
        (ulong second, int secondChunks) = RunScenario(seed: 1234);
        Assert.Equal(first, second);
        Assert.Equal(firstChunks, secondChunks);

        // The hash sees the scenario: another seed gives another world, and the scenario fills more than one chunk.
        (ulong other, _) = RunScenario(seed: 4321);
        Assert.NotEqual(first, other);
        Assert.True(firstChunks >= 4, $"The scenario only filled {firstChunks} chunks.");
    }

    /// <summary>
    /// Runs the seeded scenario on a fresh world and hashes it before each change-tick advance and at the end. The
    /// steps are spawns, despawns, adds, sets, removes and writes, with the change tick advanced every 100 steps, all
    /// chosen by a generator of the test's own, so the sequence doesn't depend on the runtime's random number generator.
    /// </summary>
    private static (ulong Hash, int Chunks) RunScenario(ulong seed)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        ulong hash = FnvOffsetBasis;
        int chunks = 0;
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            var alive = new Entity[Steps];
            int count = 0;
            ulong state = seed;
            for (int step = 0; step < Steps; step++)
            {
                if (step % 100 == 0)
                {
                    // The tick's stamps, before the next tick's moves can overwrite them.
                    hash = Hash(world, hash);
                    world.SetChangeTickForTesting((uint)(step / 100) + 1);
                }

                int roll = (int)(Next(ref state) % 100);
                int value = (int)(Next(ref state) % 100_000);
                if (count == 0 || roll < 35)
                {
                    alive[count++] = (Next(ref state) % 3) switch
                    {
                        0 => world.Spawn(new Health(value)),
                        1 => world.Spawn(new Position(value, -value), new Payload(value)),
                        _ => world.Spawn(new Health(value), new Payload(value), default(Marker)),
                    };
                    continue;
                }

                int at = (int)(Next(ref state) % (ulong)count);
                Entity entity = alive[at];
                switch (roll % 7)
                {
                    case 0:
                        world.Despawn(entity);
                        alive[at] = alive[--count];
                        break;
                    case 1:
                        if (!world.Has<Position>(entity))
                        {
                            world.Add(entity, new Position(value, value));
                        }

                        break;
                    case 2:
                        world.Set(entity, new Health(value));
                        break;
                    case 3:
                        world.Set(entity, default(Marker));
                        break;
                    case 4:
                        _ = world.Remove<Payload>(entity);
                        break;
                    case 5:
                        _ = world.Remove<Marker>(entity) || world.Remove<Health>(entity);
                        break;
                    default:
                        if (world.Has<Payload>(entity))
                        {
                            world.Write<Payload>(entity) = new Payload(value);
                        }
                        else
                        {
                            world.Set(entity, new Payload(-value));
                        }

                        break;
                }
            }

            hash = Hash(world, hash);
            for (int a = 0; a < world.ArchetypeCount; a++)
            {
                chunks += world.Archetypes[a].Chunks.ChunkCount;
            }
        }

        tracking.ThrowIfLeaks();
        return (hash, chunks);
    }

    /// <summary>
    /// Continues FNV-1a (64-bit) from <paramref name="hash"/> over the world's counts and, for every archetype in index
    /// order, its signature and every chunk's row count, Changed and Added ticks, entity ids and the bytes of each live
    /// row's columns. Chunk addresses aren't hashed: they differ between runs.
    /// </summary>
    private static unsafe ulong Hash(EcsWorld world, ulong hash)
    {
        Add(ref hash, world.EntityCount);
        Add(ref hash, world.ArchetypeCount);
        for (int a = 0; a < world.ArchetypeCount; a++)
        {
            Archetype archetype = world.Archetypes[a];
            foreach (int id in archetype.Signature.Ids)
            {
                Add(ref hash, id);
            }

            ChunkTable table = archetype.Chunks;
            Add(ref hash, table.ChunkCount);
            for (int c = 0; c < table.ChunkCount; c++)
            {
                int count = table.GetCount(c);
                Add(ref hash, count);
                foreach (uint tick in table.GetChanged(c))
                {
                    Add(ref hash, (int)tick);
                }

                foreach (uint tick in table.GetAdded(c))
                {
                    Add(ref hash, (int)tick);
                }

                for (int r = 0; r < count; r++)
                {
                    Entity entity = EntityTestSupport.EntityAt(archetype, c, r);
                    Add(ref hash, (int)entity.Index);
                    Add(ref hash, (int)entity.Generation);
                }

                ReadOnlySpan<ComponentType> columns = archetype.Layout.Columns;
                for (int column = 0; column < columns.Length; column++)
                {
                    // Sound: the column holds Capacity values of Size bytes from its offset, and count <= Capacity.
                    var bytes = new ReadOnlySpan<byte>(table.GetData(c) + archetype.Layout.ColumnOffsets[column], count * columns[column].Size);
                    foreach (byte b in bytes)
                    {
                        hash = (hash ^ b) * 1099511628211UL;
                    }
                }
            }
        }

        return hash;
    }

    private static void Add(ref ulong hash, int value)
    {
        for (int i = 0; i < 4; i++)
        {
            hash = (hash ^ (byte)(value >> (8 * i))) * 1099511628211UL;
        }
    }

    /// <summary>SplitMix64: a small generator whose sequence is fixed by its seed.</summary>
    private static ulong Next(ref ulong state)
    {
        state += 0x9E3779B97F4A7C15UL;
        ulong z = state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
