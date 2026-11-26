using System;
using System.Collections.Generic;
using BrutalEngine.Core;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using CsCheck;
using Xunit;

namespace BrutalEngine.World.Tests.Entities;

/// <summary>Random direct-API operation sequences against a reference model that shares no code with the world.</summary>
public sealed class EntityModelTests
{
    private const int Steps = 4000;

    // Steps of the growth phase: enough, at its spawn share, to fill several chunks of the Payload archetypes (a few
    // dozen rows each at the smallest chunk size). The shrink phase after it despawns rows back across the boundaries.
    private const int GrowthSteps = 2500;

    // The component bits of a spawn's mask.
    private const int PositionBit = 1;
    private const int HealthBit = 2;
    private const int PayloadBit = 4;
    private const int MarkerBit = 8;

    /// <summary>
    /// Five samples of 4,000 random steps: spawns with a random set of up to four components (data and a tag),
    /// despawns, reads (Has, Get and TryGet of a random type, present or not), writes through Write (to a type present
    /// or not) and stale access, against a dictionary model with its own first-in, first-out slot queue and dense rows
    /// per component set. After every step the world's count, each archetype's rows and chunk count, each alive
    /// entity's record, stored row id, Has answers and component values agree with the model. Each sample grows past
    /// one chunk at a high spawn share, then shrinks at a low one, so rows move within and between chunks.
    /// </summary>
    [Fact]
    public void RandomOperations_MatchModel()
    {
        Gen.Select(Gen.Int[60, 75], Gen.Int[10, 30], Gen.Int[0, 99].Array[Steps], Gen.Int.Array[Steps])
            .Sample((growthShare, shrinkShare, operations, picks) =>
            {
                // One tracker per sample: whatever the sample spawned and despawned, Dispose frees it.
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
        int chunksSeen = 0;
        for (int step = 0; step < operations.Length; step++)
        {
            int pick = picks[step] & int.MaxValue;
            int spawnShare = step < GrowthSteps ? growthShare : shrinkShare;
            int action = pick % 10;
            bool ok;
            if (operations[step] < spawnShare || model.Alive.Count == 0)
            {
                ok = Spawn(world, model, mask: pick / 10 % 16, seed: step);
            }
            else if (action < 6)
            {
                Entity entity = model.PickAlive(pick / 10);
                world.Despawn(entity);
                model.Despawn(entity);
                ok = !world.IsAlive(entity);
            }
            else if (action < 7 || model.Despawned.Count == 0)
            {
                ok = MatchesRead(world, model, model.PickAlive(pick / 10), pick / 1000);
            }
            else if (action < 8)
            {
                ok = Writes(world, model, model.PickAlive(pick / 10), pick / 1000, seed: -step);
            }
            else
            {
                ok = StaleAccessThrows(world, model.Despawned[pick / 10 % model.Despawned.Count], pick / 1000);
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

        // The sample exercised what it is for: an archetype with rows in a second chunk.
        return chunksSeen >= 2;
    }

    private static bool Spawn(EcsWorld world, Model model, int mask, int seed)
    {
        // Every component set, with the type arguments in varied orders.
        Entity entity = mask switch
        {
            0 => world.Spawn(),
            1 => world.Spawn(NewPosition(seed)),
            2 => world.Spawn(NewHealth(seed)),
            3 => world.Spawn(NewHealth(seed), NewPosition(seed)),
            4 => world.Spawn(new Payload(seed)),
            5 => world.Spawn(NewPosition(seed), new Payload(seed)),
            6 => world.Spawn(new Payload(seed), NewHealth(seed)),
            7 => world.Spawn(NewPosition(seed), NewHealth(seed), new Payload(seed)),
            8 => world.Spawn(default(Marker)),
            9 => world.Spawn(default(Marker), NewPosition(seed)),
            10 => world.Spawn(NewHealth(seed), default(Marker)),
            11 => world.Spawn(NewPosition(seed), default(Marker), NewHealth(seed)),
            12 => world.Spawn(new Payload(seed), default(Marker)),
            13 => world.Spawn(default(Marker), new Payload(seed), NewPosition(seed)),
            14 => world.Spawn(NewHealth(seed), new Payload(seed), default(Marker)),
            _ => world.Spawn(default(Marker), new Payload(seed), NewHealth(seed), NewPosition(seed)),
        };

        if (entity != model.Spawn(mask, seed) || !world.Entities.TryGetLocation(entity, out EntityRecord record))
        {
            return false;
        }

        // Each component set has one archetype: learn its index on first use, and require the same one afterwards.
        if (!model.Archetypes.TryGetValue(mask, out int archetype))
        {
            if (model.Archetypes.ContainsValue(record.Archetype))
            {
                return false;
            }

            model.Archetypes.Add(mask, record.Archetype);
            archetype = record.Archetype;
        }

        return record.Archetype == archetype;
    }

    /// <summary>One read of a random type on an alive entity: Has, Get (which throws for a missing type) or TryGet.</summary>
    private static bool MatchesRead(EcsWorld world, Model model, Entity entity, int pick)
    {
        (int mask, int[] seeds) = model.Alive[entity];
        int bit = 1 << (pick % 4);
        int seed = bit == MarkerBit ? 0 : seeds[SeedIndex(bit)];
        bool has = (mask & bit) != 0;
        switch (pick / 4 % 3)
        {
            case 0:
                return HasOf(world, entity, bit) == has;
            case 1 when bit == MarkerBit:
                return Record.Exception(() => world.Get<Marker>(entity)) is InvalidOperationException;
            case 1 when !has:
                return Record.Exception(() => GetAnyOf(world, entity, bit)) is InvalidOperationException;
            case 1:
                return ValuesMatch(world, entity, bit, seed);
            default:
                return bit switch
                {
                    PositionBit => world.TryGet(entity, out Position position) == has
                        && EntityComponents.Same(position, has ? NewPosition(seed) : default),
                    HealthBit => world.TryGet(entity, out Health health) == has
                        && EntityComponents.Same(health, has ? NewHealth(seed) : default),
                    PayloadBit => world.TryGet(entity, out Payload payload) == has
                        && EntityComponents.Same(payload, has ? new Payload(seed) : default),
                    _ => Record.Exception(() => world.TryGet(entity, out Marker _)) is InvalidOperationException,
                };
        }
    }

    /// <summary>
    /// One write of a random type on an alive entity through Write: a new value for a data component it has, or an
    /// exception for one it lacks or a tag.
    /// </summary>
    private static bool Writes(EcsWorld world, Model model, Entity entity, int pick, int seed)
    {
        int bit = 1 << (pick % 4);
        if ((model.Alive[entity].Mask & bit) == 0 || bit == MarkerBit)
        {
            Action write = bit switch
            {
                PositionBit => () => world.Write<Position>(entity),
                HealthBit => () => world.Write<Health>(entity),
                PayloadBit => () => world.Write<Payload>(entity),
                _ => () => world.Write<Marker>(entity),
            };
            return Record.Exception(write) is InvalidOperationException;
        }

        switch (bit)
        {
            case PositionBit:
                world.Write<Position>(entity) = NewPosition(seed);
                break;
            case HealthBit:
                world.Write<Health>(entity) = NewHealth(seed);
                break;
            default:
                world.Write<Payload>(entity) = new Payload(seed);
                break;
        }

        model.Write(entity, bit, seed);
        return true;
    }

    /// <summary>A despawned entity throws StaleHandleException naming its handle from every member.</summary>
    private static bool StaleAccessThrows(EcsWorld world, Entity stale, int pick)
    {
        Action access = (pick % 5) switch
        {
            0 => () => world.Despawn(stale),
            1 => () => world.Has<Position>(stale),
            2 => () => world.Get<Health>(stale),
            3 => () => world.Write<Health>(stale),
            _ => () => world.TryGet(stale, out Position _),
        };
        return Record.Exception(access) is StaleHandleException exception
            && exception.Handle == new Handle(stale.Index, stale.Generation);
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
        for (int bit = 1; bit <= MarkerBit; bit <<= 1)
        {
            bool has = (mask & bit) != 0;
            if (HasOf(world, entity, bit) != has
                || (has && bit != MarkerBit && !ValuesMatch(world, entity, bit, seeds[SeedIndex(bit)])))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasOf(EcsWorld world, Entity entity, int bit) => bit switch
    {
        PositionBit => world.Has<Position>(entity),
        HealthBit => world.Has<Health>(entity),
        PayloadBit => world.Has<Payload>(entity),
        _ => world.Has<Marker>(entity),
    };

    private static void GetAnyOf(EcsWorld world, Entity entity, int bit)
    {
        switch (bit)
        {
            case PositionBit:
                _ = world.Get<Position>(entity);
                break;
            case HealthBit:
                _ = world.Get<Health>(entity);
                break;
            default:
                _ = world.Get<Payload>(entity);
                break;
        }
    }

    private static bool ValuesMatch(EcsWorld world, Entity entity, int bit, int seed) => bit switch
    {
        PositionBit => EntityComponents.Same(world.Get<Position>(entity), NewPosition(seed)),
        HealthBit => EntityComponents.Same(world.Get<Health>(entity), NewHealth(seed)),
        _ => EntityComponents.Same(world.Get<Payload>(entity), new Payload(seed)),
    };

    private static int SeedIndex(int bit) => bit switch
    {
        PositionBit => 0,
        HealthBit => 1,
        _ => 2,
    };

    private static Position NewPosition(int seed) => new(seed, -seed);

    private static Health NewHealth(int seed) => new(seed * 3);

    /// <summary>
    /// The reference model: slot generations with a first-in, first-out free queue, each alive entity's component set
    /// and value seeds, and each component set's dense rows, where a removal moves the last row into the hole.
    /// </summary>
    private sealed class Model
    {
        private readonly List<uint> _generations = [];
        private readonly Queue<int> _free = new();
        private readonly List<Entity> _alive = [];

        /// <summary>
        /// Alive entities: their component set (a mask of the component bits) and the seed of each data component's
        /// value (Position, Health, Payload).
        /// </summary>
        public Dictionary<Entity, (int Mask, int[] Seeds)> Alive { get; } = [];

        /// <summary>Each component set's rows, in storage order.</summary>
        public Dictionary<int, List<Entity>> Rows { get; } = [];

        /// <summary>Each component set's archetype index, learned from the world on first use; the empty set is 0.</summary>
        public Dictionary<int, int> Archetypes { get; } = new() { [0] = 0 };

        /// <summary>Every entity despawned so far.</summary>
        public List<Entity> Despawned { get; } = [];

        /// <summary>An alive entity, chosen by <paramref name="pick"/>.</summary>
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
            if (!Rows.TryGetValue(mask, out List<Entity>? rows))
            {
                rows = [];
                Rows.Add(mask, rows);
            }

            rows.Add(entity);
            return entity;
        }

        public void Write(Entity entity, int bit, int seed) => Alive[entity].Seeds[SeedIndex(bit)] = seed;

        public void Despawn(Entity entity)
        {
            List<Entity> rows = Rows[Alive[entity].Mask];
            int row = rows.IndexOf(entity);
            rows[row] = rows[^1];
            rows.RemoveAt(rows.Count - 1);
            _ = Alive.Remove(entity);
            int at = _alive.IndexOf(entity);
            _alive[at] = _alive[^1];
            _alive.RemoveAt(_alive.Count - 1);
            Despawned.Add(entity);
            _generations[(int)entity.Index]++;
            _free.Enqueue((int)entity.Index);
        }
    }
}
