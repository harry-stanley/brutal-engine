using System.Collections.Generic;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using BrutalEngine.World.Tests.Structural;
using BrutalEngine.World.Ticks;
using CsCheck;
using Xunit;

namespace BrutalEngine.World.Tests.Ticks;

// The property over repeated clamps, against a reference model that counts ticks in 64 bits and so never wraps.
public sealed partial class ClampTests
{
    // Random steps per phase: before the jump towards a boundary, after it, and after the crossing.
    private const int StepsBeforeJump = 30;
    private const int StepsAfterJump = 30;
    private const int StepsAfterCrossing = 10;
    private const int Rounds = 3;
    private const int StepsPerRound = StepsBeforeJump + StepsAfterJump + StepsAfterCrossing;

    /// <summary>
    /// Random writes, advances, consumer registrations, runs and releases across three boundaries, starting at the
    /// first, second, third or fourth (so one sample in four crosses the wrap at 2³² first, and every start but the
    /// last crosses it later). After every step: the world's tick and every stored chunk and consumer tick equal the
    /// low 32 bits of the model's, whose clamp raises old values in 64-bit arithmetic; every wrap-safe comparison of a
    /// stamp with a consumer agrees with the 64-bit one; no change that happened after a consumer's last run is ever
    /// reported as not newer; and a change that didn't is reported only to a consumer the clamp raised.
    /// </summary>
    [Fact]
    public void ThreeBoundaries_InvariantsHold()
    {
        Gen.Select(Gen.Int[1, 4], Gen.Int[0, 99].Array[Rounds * StepsPerRound], Gen.Int[0, int.MaxValue].Array[Rounds * StepsPerRound], Gen.Int[1, 4])
            .Sample((startBoundary, operations, picks, jumpShort) =>
            {
                TrackingAllocator tracking = EntityTestSupport.NewTracking();
                using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
                {
                    RunModel(world, startBoundary, operations, picks, jumpShort);
                }

                tracking.ThrowIfLeaks();
            }, iter: 60, threads: 1);
    }

    private static void RunModel(EcsWorld world, int startBoundary, int[] operations, int[] picks, int jumpShort)
    {
        // A start other than tick 1 is set before anything is stamped, as if the world had run that long.
        long start = ((startBoundary - 1) * (long)Interval) + 1;
        world.SetChangeTickForTesting((uint)start);
        TickModel model = new(world, start);
        model.Verify();

        int step = 0;
        for (int round = 0; round < Rounds; round++)
        {
            long target = (startBoundary + round) * (long)Interval;
            for (int i = 0; i < StepsBeforeJump; i++, step++)
            {
                model.Step(operations[step], picks[step]);
            }

            // Jump to just short of the boundary; no boundary lies between, so skipping the clamp loses nothing.
            long near = target - jumpShort;
            Assert.True(near > model.Tick, "The steps before the jump must not reach the boundary.");
            world.SetChangeTickForTesting((uint)near);
            model.Tick = near;
            model.Verify();

            for (int i = 0; i < StepsAfterJump; i++, step++)
            {
                model.Step(operations[step], picks[step]);
            }

            while (model.Tick < target)
            {
                model.Advance();
            }

            Assert.Equal(target / Interval, model.Clamps + startBoundary - 1);
            for (int i = 0; i < StepsAfterCrossing; i++, step++)
            {
                model.Step(operations[step], picks[step]);
            }
        }

        Assert.Equal(Rounds, model.Clamps);
    }

    /// <summary>
    /// The model: every chunk tick (per archetype, chunk, column, <c>Changed</c> or <c>Added</c>) and every consumer
    /// as a 64-bit value with the same clamp rule, plus its true, never-clamped value. Entities don't move after the
    /// setup, so each one's chunk and columns stay put.
    /// </summary>
    private sealed class TickModel
    {
        private readonly EcsWorld _world;
        private readonly List<Entity> _entities = [];
        private readonly Dictionary<(int Archetype, int Chunk, int Column, bool Added), Stamp> _stamps = [];
        private readonly Dictionary<int, Consumer> _consumers = [];
        private readonly List<int> _live = [];

        public TickModel(EcsWorld world, long start)
        {
            _world = world;
            Tick = start;
            StructuralSupport.RegisterInOrder(world);

            // Three archetypes; the Payload one has two chunks. Health's id (1) sorts between Position (0) and Payload (2).
            for (int i = 0; i < 3; i++)
            {
                _entities.Add(world.Spawn(new Position(i, i)));
                _entities.Add(world.Spawn(new Health(i), new Position(i, i)));
            }

            Entity firstPayload = world.Spawn(new Payload(0), new Position(0, 0));
            _entities.Add(firstPayload);
            Archetype payloads = StructuralSupport.ArchetypeOf(world, firstPayload);
            while (payloads.Chunks.ChunkCount < 2)
            {
                _entities.Add(world.Spawn(new Payload(1), new Position(1, 1)));
            }

            for (int a = 0; a < world.ArchetypeCount; a++)
            {
                ChunkTable chunks = world.Archetypes[a].Chunks;
                for (int c = 0; c < chunks.ChunkCount; c++)
                {
                    for (int column = 0; column < chunks.ColumnCount; column++)
                    {
                        _stamps[(a, c, column, false)] = new Stamp(start);
                        _stamps[(a, c, column, true)] = new Stamp(start);
                    }
                }
            }
        }

        public long Tick { get; set; }

        public int Clamps { get; private set; }

        public void Step(int operation, int pick)
        {
            switch (operation)
            {
                case < 35:
                    Write(pick);
                    break;
                case < 60:
                    Advance();
                    break;
                case < 75:
                    Register();
                    break;
                case < 90:
                    Run(pick);
                    break;
                default:
                    Release(pick);
                    break;
            }

            Verify();
        }

        public void Advance()
        {
            _world.AdvanceChangeTick();
            Tick++;
            if (Tick % Interval == 0)
            {
                Clamps++;
                long stampFloor = Tick - Interval + 1;
                foreach (Stamp stamp in _stamps.Values)
                {
                    stamp.Model = long.Max(stamp.Model, stampFloor);
                }

                foreach (Consumer consumer in _consumers.Values)
                {
                    if (consumer.Model < stampFloor)
                    {
                        consumer.Model = stampFloor - 1;
                        consumer.Clamped = true;
                    }
                }
            }

            Verify();
        }

        public void Verify()
        {
            Assert.Equal((uint)Tick, _world.ChangeTick);
            foreach (KeyValuePair<(int Archetype, int Chunk, int Column, bool Added), Stamp> entry in _stamps)
            {
                ChunkTable chunks = _world.Archetypes[entry.Key.Archetype].Chunks;
                uint stored = entry.Key.Added
                    ? chunks.GetAdded(entry.Key.Chunk)[entry.Key.Column]
                    : chunks.GetChanged(entry.Key.Chunk)[entry.Key.Column];
                Assert.Equal((uint)entry.Value.Model, stored);
                Assert.InRange(Tick - entry.Value.Model, 0, int.MaxValue);
            }

            foreach (KeyValuePair<int, Consumer> entry in _consumers)
            {
                Consumer consumer = entry.Value;
                Assert.Equal((uint)consumer.Model, _world.TickConsumers.Get(entry.Key));
                Assert.InRange(Tick - consumer.Model, 0, int.MaxValue);
                foreach (Stamp stamp in _stamps.Values)
                {
                    bool newer = TickMath.IsNewer((uint)stamp.Model, (uint)consumer.Model);
                    Assert.Equal(stamp.Model > consumer.Model, newer);
                    if (stamp.Truth > consumer.Truth)
                    {
                        Assert.True(newer, "A change made after the consumer's last run was lost.");
                    }
                    else if (newer)
                    {
                        Assert.True(consumer.Clamped, "A false positive for a consumer the clamp never raised.");
                    }
                }
            }

            Assert.Equal(_consumers.Count, _world.TickConsumers.Count);
        }

        private void Write(int pick)
        {
            Entity entity = _entities[pick % _entities.Count];
            Archetype archetype = StructuralSupport.ArchetypeOf(_world, entity);
            int column;
            switch (pick / _entities.Count % 2)
            {
                case 0:
                    _world.Write<Position>(entity);
                    column = TickTestSupport.ColumnOf<Position>(_world, archetype);
                    break;
                default:
                    if (_world.Has<Payload>(entity))
                    {
                        _world.Write<Payload>(entity);
                        column = TickTestSupport.ColumnOf<Payload>(_world, archetype);
                    }
                    else if (_world.Has<Health>(entity))
                    {
                        _world.Write<Health>(entity);
                        column = TickTestSupport.ColumnOf<Health>(_world, archetype);
                    }
                    else
                    {
                        _world.Write<Position>(entity);
                        column = TickTestSupport.ColumnOf<Position>(_world, archetype);
                    }

                    break;
            }

            Stamp stamp = _stamps[(archetype.Index, StructuralSupport.Location(_world, entity).Chunk, column, false)];
            stamp.Model = Tick;
            stamp.Truth = Tick;
        }

        private void Register()
        {
            int slot = _world.TickConsumers.Register((uint)Tick);
            Assert.False(_consumers.ContainsKey(slot), "A live slot was handed out again.");
            _consumers[slot] = new Consumer(Tick);
            _live.Add(slot);
        }

        private void Run(int pick)
        {
            if (_live.Count == 0)
            {
                return;
            }

            int slot = _live[pick % _live.Count];
            _world.TickConsumers.Set(slot, (uint)Tick);
            _consumers[slot] = new Consumer(Tick);
        }

        private void Release(int pick)
        {
            if (_live.Count == 0)
            {
                return;
            }

            int index = pick % _live.Count;
            int slot = _live[index];
            _world.TickConsumers.Release(slot);
            _consumers.Remove(slot);
            _live.RemoveAt(index);
        }
    }

    private sealed class Stamp(long tick)
    {
        public long Model { get; set; } = tick;

        public long Truth { get; set; } = tick;
    }

    private sealed class Consumer(long tick)
    {
        public long Model { get; set; } = tick;

        public long Truth { get; } = tick;

        public bool Clamped { get; set; }
    }
}
