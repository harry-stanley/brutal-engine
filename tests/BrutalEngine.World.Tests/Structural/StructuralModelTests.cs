using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using CsCheck;
using Xunit;

namespace BrutalEngine.World.Tests.Structural;

/// <summary>
/// Random sequences of spawns, despawns, adds, sets, removes, reads and stale access against a reference model that
/// shares no code with the world: its own slot generations and first-in, first-out free queue, and its own dense
/// rows per component set, where a move appends to the target set's rows and fills the hole in the source's with its
/// last row.
/// </summary>
public sealed class StructuralModelTests
{
    private const int Steps = 4000;

    // Steps of the growth phase, at a high spawn share; the shrink phase after it despawns rows back across chunks.
    private const int GrowthSteps = 2500;

    // The component bits of a component set.
    private const int PositionBit = 1;
    private const int HealthBit = 2;
    private const int PayloadBit = 4;
    private const int MarkerBit = 8;

    /// <summary>
    /// Five samples of 4,000 random steps. After every step the world's entity count, each component set's archetype
    /// (one per set, the same one every time), its row and chunk counts, each alive entity's record and stored id,
    /// its Has answers and its component values agree with the model, and each sample leaves no native block behind.
    /// </summary>
    [Fact]
    public void WithStructural_MatchModel()
    {
        Gen.Select(Gen.Int[60, 75], Gen.Int[5, 20], Gen.Int[0, 99].Array[Steps], Gen.Int.Array[Steps])
            .Sample((growthShare, shrinkShare, operations, picks) =>
            {
                TrackingAllocator tracking = EntityTestSupport.NewTracking();
                bool matches;
                using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
                {
                    matches = MatchesModel(world, growthShare, shrinkShare, operations, picks);
                }

                tracking.ThrowIfLeaks();
                return matches;
            }, iter: 5, threads: 1);
    }

    private static bool MatchesModel(EcsWorld world, int growthShare, int shrinkShare, int[] operations, int[] picks)
    {
        Model model = new();
        Reached reached = new();
        int chunksSeen = 0;
        int moves = 0;
        for (int step = 0; step < operations.Length; step++)
        {
            int pick = picks[step] & int.MaxValue;
            int spawnShare = step < GrowthSteps ? growthShare : shrinkShare;
            int bit = 1 << (pick / 10 % 4);
            int seed = step + 1;
            bool ok;
            if (operations[step] < spawnShare || model.Alive.Count == 0)
            {
                ok = Spawn(world, model, mask: pick / 10 % 16, seed);
            }
            else
            {
                Entity entity = model.PickAlive(pick / 40);
                int before = model.Alive[entity].Mask;
                ok = (pick % 10) switch
                {
                    0 or 1 => Despawn(world, model, entity),
                    2 or 3 => Add(world, model, reached, entity, bit, seed),
                    4 or 5 => Set(world, model, reached, entity, bit, seed),
                    6 or 7 => Remove(world, model, reached, entity, bit),
                    8 => Reads(world, model, entity, bit),
                    _ => model.Despawned.Count == 0 || StaleAccessThrows(world, reached, model.Despawned[pick / 40 % model.Despawned.Count], pick / 40),
                };

                if (model.Alive.TryGetValue(entity, out (int Mask, int[] Seeds) after) && after.Mask != before)
                {
                    moves++;
                }
            }

            if (!ok || !MatchesState(world, model))
            {
                return false;
            }

            foreach (int archetype in model.Archetypes.Values)
            {
                chunksSeen = Math.Max(chunksSeen, world.Archetypes[archetype].Chunks.ChunkCount);
            }
        }

        // The sample exercised what it is for: rows in a second chunk, many moves, and each operation that doesn't move.
        return chunksSeen >= 2 && moves >= 100
            && reached.SetsInPlace > 0 && reached.FailingAdds > 0 && reached.AbsentRemoves > 0 && reached.StaleAccesses > 0;
    }

    private static bool Spawn(EcsWorld world, Model model, int mask, int seed)
    {
        Entity entity = mask switch
        {
            0 => world.Spawn(),
            1 => world.Spawn(NewPosition(seed)),
            2 => world.Spawn(NewHealth(seed)),
            3 => world.Spawn(NewPosition(seed), NewHealth(seed)),
            4 => world.Spawn(new Payload(seed)),
            5 => world.Spawn(new Payload(seed), NewPosition(seed)),
            6 => world.Spawn(NewHealth(seed), new Payload(seed)),
            7 => world.Spawn(new Payload(seed), NewHealth(seed), NewPosition(seed)),
            8 => world.Spawn(default(Marker)),
            9 => world.Spawn(NewPosition(seed), default(Marker)),
            10 => world.Spawn(default(Marker), NewHealth(seed)),
            11 => world.Spawn(NewHealth(seed), default(Marker), NewPosition(seed)),
            12 => world.Spawn(default(Marker), new Payload(seed)),
            13 => world.Spawn(new Payload(seed), NewPosition(seed), default(Marker)),
            14 => world.Spawn(new Payload(seed), default(Marker), NewHealth(seed)),
            _ => world.Spawn(NewHealth(seed), default(Marker), NewPosition(seed), new Payload(seed)),
        };

        return entity == model.Spawn(mask, seed) && LearnArchetype(world, model, entity);
    }

    private static bool Despawn(EcsWorld world, Model model, Entity entity)
    {
        world.Despawn(entity);
        model.Despawn(entity);
        return !world.IsAlive(entity);
    }

    /// <summary>Add: a move for an absent component; for a present one, InvalidOperationException naming Set.</summary>
    private static bool Add(EcsWorld world, Model model, Reached reached, Entity entity, int bit, int seed)
    {
        Action add = bit switch
        {
            PositionBit => () => world.Add(entity, NewPosition(seed)),
            HealthBit => () => world.Add(entity, NewHealth(seed)),
            PayloadBit => () => world.Add(entity, new Payload(seed)),
            _ => () => world.Add(entity, default(Marker)),
        };

        if ((model.Alive[entity].Mask & bit) != 0)
        {
            reached.FailingAdds++;
            return Record.Exception(add) is InvalidOperationException failure
                && failure.Message.Contains("already has", StringComparison.Ordinal);
        }

        add();
        model.Move(entity, model.Alive[entity].Mask | bit, bit, seed);
        return LearnArchetype(world, model, entity);
    }

    /// <summary>Set: a replacement in place for a present component (none for a tag), a move for an absent one.</summary>
    private static bool Set(EcsWorld world, Model model, Reached reached, Entity entity, int bit, int seed)
    {
        switch (bit)
        {
            case PositionBit:
                world.Set(entity, NewPosition(seed));
                break;
            case HealthBit:
                world.Set(entity, NewHealth(seed));
                break;
            case PayloadBit:
                world.Set(entity, new Payload(seed));
                break;
            default:
                world.Set(entity, default(Marker));
                break;
        }

        int mask = model.Alive[entity].Mask;
        if ((mask & bit) != 0)
        {
            reached.SetsInPlace++;
            model.Replace(entity, bit, seed);
            return true;
        }

        model.Move(entity, mask | bit, bit, seed);
        return LearnArchetype(world, model, entity);
    }

    /// <summary>Remove: a move for a present component, false and no change for an absent one.</summary>
    private static bool Remove(EcsWorld world, Model model, Reached reached, Entity entity, int bit)
    {
        bool removed = bit switch
        {
            PositionBit => world.Remove<Position>(entity),
            HealthBit => world.Remove<Health>(entity),
            PayloadBit => world.Remove<Payload>(entity),
            _ => world.Remove<Marker>(entity),
        };

        int mask = model.Alive[entity].Mask;
        if ((mask & bit) == 0)
        {
            reached.AbsentRemoves++;
            return !removed;
        }

        model.Move(entity, mask & ~bit, bit: 0, seed: 0);
        return removed && LearnArchetype(world, model, entity);
    }

    /// <summary>Has, TryGet and Get of one type, present or not.</summary>
    private static bool Reads(EcsWorld world, Model model, Entity entity, int bit)
    {
        (int mask, int[] seeds) = model.Alive[entity];
        bool has = (mask & bit) != 0;
        int seed = bit == MarkerBit ? 0 : seeds[SeedIndex(bit)];
        return bit switch
        {
            PositionBit => world.TryGet(entity, out Position position) == has
                && EntityComponents.Same(position, has ? NewPosition(seed) : default)
                && (has || Record.Exception(() => world.Get<Position>(entity)) is InvalidOperationException),
            HealthBit => world.TryGet(entity, out Health health) == has
                && EntityComponents.Same(health, has ? NewHealth(seed) : default)
                && (has || Record.Exception(() => world.Get<Health>(entity)) is InvalidOperationException),
            PayloadBit => world.TryGet(entity, out Payload payload) == has
                && EntityComponents.Same(payload, has ? new Payload(seed) : default)
                && (has || Record.Exception(() => world.Get<Payload>(entity)) is InvalidOperationException),
            _ => world.Has<Marker>(entity) == has,
        };
    }

    /// <summary>A despawned entity throws StaleHandleException naming its handle from every member, structural ones included.</summary>
    private static bool StaleAccessThrows(EcsWorld world, Reached reached, Entity stale, int pick)
    {
        reached.StaleAccesses++;
        Action access = (pick % 7) switch
        {
            0 => () => world.Despawn(stale),
            1 => () => world.Has<Position>(stale),
            2 => () => world.Get<Health>(stale),
            3 => () => world.TryGet(stale, out Payload _),
            4 => () => world.Add(stale, default(Marker)),
            5 => () => world.Set(stale, NewHealth(1)),
            _ => () => world.Remove<Position>(stale),
        };
        return Record.Exception(access) is StaleHandleException exception
            && exception.Handle == new Handle(stale.Index, stale.Generation);
    }

    /// <summary>
    /// Each component set has one archetype: learn its index the first time an entity lands in it, require a set never
    /// seen before to get an archetype no other set has, and the same archetype every time after.
    /// </summary>
    private static bool LearnArchetype(EcsWorld world, Model model, Entity entity)
    {
        if (!world.Entities.TryGetLocation(entity, out EntityRecord record))
        {
            return false;
        }

        int mask = model.Alive[entity].Mask;
        if (!model.Archetypes.TryGetValue(mask, out int archetype))
        {
            if (model.Archetypes.ContainsValue(record.Archetype))
            {
                return false;
            }

            model.Archetypes.Add(mask, record.Archetype);
            return true;
        }

        return record.Archetype == archetype;
    }

    private static bool MatchesState(EcsWorld world, Model model)
    {
        if (world.EntityCount != model.Alive.Count)
        {
            return false;
        }

        foreach ((int mask, List<Entity> rows) in model.Rows)
        {
            Archetype archetype = world.Archetypes[model.Archetypes[mask]];
            int capacity = archetype.Layout.Capacity;
            if (archetype.RowCount != rows.Count || archetype.Chunks.ChunkCount != (rows.Count + capacity - 1) / capacity)
            {
                return false;
            }

            for (int i = 0; i < rows.Count; i++)
            {
                Entity entity = rows[i];
                if (!world.Entities.TryGetLocation(entity, out EntityRecord record)
                    || record.Archetype != archetype.Index || record.Chunk != i / capacity || record.Row != i % capacity
                    || EntityTestSupport.EntityAt(archetype, i / capacity, i % capacity) != entity
                    || !EntityMatches(world, entity, mask, model.Alive[entity].Seeds))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool EntityMatches(EcsWorld world, Entity entity, int mask, int[] seeds)
    {
        if (world.Has<Position>(entity) != ((mask & PositionBit) != 0)
            || world.Has<Health>(entity) != ((mask & HealthBit) != 0)
            || world.Has<Payload>(entity) != ((mask & PayloadBit) != 0)
            || world.Has<Marker>(entity) != ((mask & MarkerBit) != 0))
        {
            return false;
        }

        return ((mask & PositionBit) == 0 || EntityComponents.Same(world.Get<Position>(entity), NewPosition(seeds[0])))
            && ((mask & HealthBit) == 0 || EntityComponents.Same(world.Get<Health>(entity), NewHealth(seeds[1])))
            && ((mask & PayloadBit) == 0 || EntityComponents.Same(world.Get<Payload>(entity), new Payload(seeds[2])));
    }

    private static int SeedIndex(int bit) => bit switch
    {
        PositionBit => 0,
        HealthBit => 1,
        _ => 2,
    };

    private static Position NewPosition(int seed) => new(seed, -seed);

    private static Health NewHealth(int seed) => new(seed * 7);

    /// <summary>How often a sample took each path that changes nothing or fails, so a sample that never does fails.</summary>
    private sealed class Reached
    {
        public int SetsInPlace { get; set; }

        public int FailingAdds { get; set; }

        public int AbsentRemoves { get; set; }

        public int StaleAccesses { get; set; }
    }

    /// <summary>
    /// The reference model: slot generations with a first-in, first-out free queue, each alive entity's component set
    /// and value seeds, and each component set's dense rows.
    /// </summary>
    private sealed class Model
    {
        private readonly List<uint> _generations = [];
        private readonly Queue<int> _free = new();
        private readonly List<Entity> _alive = [];

        /// <summary>Alive entities: their component set and the seed of each data component's value.</summary>
        public Dictionary<Entity, (int Mask, int[] Seeds)> Alive { get; } = [];

        /// <summary>Each component set's rows, in storage order.</summary>
        public Dictionary<int, List<Entity>> Rows { get; } = [];

        /// <summary>Each component set's archetype index, learned from the world on first use; the empty set is 0.</summary>
        public Dictionary<int, int> Archetypes { get; } = new() { [0] = 0 };

        /// <summary>Every entity despawned so far.</summary>
        public List<Entity> Despawned { get; } = [];

        public Entity PickAlive(int pick) => _alive[pick % _alive.Count];

        public Entity Spawn(int mask, int seed)
        {
            if (!_free.TryDequeue(out int index))
            {
                index = _generations.Count;
                _generations.Add(1);
            }

            var entity = new Entity((uint)index, _generations[index]);
            Alive.Add(entity, (mask, [seed, seed, seed]));
            _alive.Add(entity);
            RowsOf(mask).Add(entity);
            return entity;
        }

        /// <summary>Moves the entity to <paramref name="mask"/>'s rows, with <paramref name="seed"/> for an added data component.</summary>
        public void Move(Entity entity, int mask, int bit, int seed)
        {
            (int from, int[] seeds) = Alive[entity];
            RemoveRow(RowsOf(from), entity);
            RowsOf(mask).Add(entity);
            Alive[entity] = (mask, seeds);
            if (bit is PositionBit or HealthBit or PayloadBit)
            {
                seeds[SeedIndex(bit)] = seed;
            }
        }

        /// <summary>Replaces a present component's value; a tag has none.</summary>
        public void Replace(Entity entity, int bit, int seed)
        {
            if (bit != MarkerBit)
            {
                Alive[entity].Seeds[SeedIndex(bit)] = seed;
            }
        }

        public void Despawn(Entity entity)
        {
            RemoveRow(Rows[Alive[entity].Mask], entity);
            _ = Alive.Remove(entity);
            RemoveRow(_alive, entity);
            Despawned.Add(entity);
            _generations[(int)entity.Index]++;
            _free.Enqueue((int)entity.Index);
        }

        /// <summary>Removes a row by moving the last row into its place.</summary>
        private static void RemoveRow(List<Entity> rows, Entity entity)
        {
            int row = rows.IndexOf(entity);
            rows[row] = rows[^1];
            rows.RemoveAt(rows.Count - 1);
        }

        private List<Entity> RowsOf(int mask)
        {
            if (!Rows.TryGetValue(mask, out List<Entity>? rows))
            {
                rows = [];
                Rows.Add(mask, rows);
            }

            return rows;
        }
    }
}
