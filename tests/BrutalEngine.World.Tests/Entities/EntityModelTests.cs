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

    // Steps of the growth phase: at a spawn share of 90% or more, enough to fill more than one chunk of the empty
    // archetype (2,048 rows at the smallest chunk size). The shrink phase after it despawns rows back across the boundary.
    private const int GrowthSteps = 3000;

    /// <summary>
    /// Five samples of 4,000 random spawns, despawns and stale despawns, against a dictionary model with its own
    /// first-in, first-out slot queue and dense row list. After every step the world's count, each alive entity's
    /// liveness, record and stored row, the chunk count, and the touched entity agree with the model. Each sample grows
    /// past one chunk at a high spawn share, then shrinks at a low one, so rows move within and between chunks.
    /// </summary>
    [Fact]
    public void RandomOperations_MatchModel()
    {
        Gen.Select(Gen.Int[90, 94], Gen.Int[10, 40], Gen.Int[0, 99].Array[Steps], Gen.Int.Array[Steps])
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
            int operation = operations[step];
            int spawnShare = step < GrowthSteps ? growthShare : shrinkShare;
            Entity touched;
            if (operation < spawnShare || model.Rows.Count == 0)
            {
                touched = world.Spawn();
                if (touched != model.Spawn())
                {
                    return false;
                }
            }
            else if (operation < 95 || model.Despawned.Count == 0)
            {
                touched = model.Rows[pick % model.Rows.Count];
                world.Despawn(touched);
                model.Despawn(touched);
            }
            else
            {
                // A stale entity throws, naming its handle, and changes nothing.
                touched = model.Despawned[pick % model.Despawned.Count];
                if (Record.Exception(() => world.Despawn(touched)) is not StaleHandleException stale
                    || stale.Handle != new Handle(touched.Index, touched.Generation))
                {
                    return false;
                }
            }

            if (world.IsAlive(touched) != model.Alive.ContainsKey(touched) || !MatchesState(world, model))
            {
                return false;
            }

            chunksSeen = Math.Max(chunksSeen, world.ChunkCount);
        }

        // The sample exercised what it is for: rows in a second chunk.
        return chunksSeen >= 2;
    }

    private static bool MatchesState(EcsWorld world, Model model)
    {
        Archetype empty = world.Archetypes.Empty;
        int capacity = empty.Layout.Capacity;
        if (world.EntityCount != model.Rows.Count
            || empty.RowCount != model.Rows.Count
            || world.ChunkCount != (model.Rows.Count + capacity - 1) / capacity)
        {
            return false;
        }

        for (int i = 0; i < model.Rows.Count; i++)
        {
            Entity entity = model.Rows[i];
            int chunk = i / capacity;
            int row = i % capacity;
            if (!world.Entities.TryGetLocation(entity, out EntityRecord record)
                || record.Archetype != 0 || record.Chunk != chunk || record.Row != row
                || EntityTestSupport.EntityAt(empty, chunk, row) != entity)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The reference model: slot generations with a first-in, first-out free queue, the alive entities, and the empty
    /// archetype's dense rows, where a removal moves the last row into the hole.
    /// </summary>
    private sealed class Model
    {
        private readonly List<uint> _generations = [];
        private readonly Queue<int> _free = new();

        /// <summary>Alive entities and their row in <see cref="Rows"/>.</summary>
        public Dictionary<Entity, int> Alive { get; } = [];

        /// <summary>The archetype's rows, in storage order.</summary>
        public List<Entity> Rows { get; } = [];

        /// <summary>Every entity despawned so far.</summary>
        public List<Entity> Despawned { get; } = [];

        public Entity Spawn()
        {
            int index;
            if (!_free.TryDequeue(out index))
            {
                index = _generations.Count;
                _generations.Add(1);
            }

            var entity = new Entity((uint)index, _generations[index]);
            Alive.Add(entity, Rows.Count);
            Rows.Add(entity);
            return entity;
        }

        public void Despawn(Entity entity)
        {
            int row = Alive[entity];
            _ = Alive.Remove(entity);
            Entity last = Rows[^1];
            Rows.RemoveAt(Rows.Count - 1);
            if (last != entity)
            {
                Rows[row] = last;
                Alive[last] = row;
            }

            Despawned.Add(entity);
            _generations[(int)entity.Index]++;
            _free.Enqueue((int)entity.Index);
        }
    }
}
