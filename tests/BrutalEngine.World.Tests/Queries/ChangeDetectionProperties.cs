using System.Collections.Generic;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Tests.Entities;
using CsCheck;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// Random spawns, writes, tick advances and enumerations (complete, ended early, writing in the loop) against a model of
/// chunk ticks and query last runs (docs/design/world.md, section 5, "Change detection" and "Standalone queries").
/// Only spawns change rows, so the n-th entity spawned sits in chunk n / capacity, and the model needs no storage.
/// </summary>
public sealed class ChangeDetectionProperties
{
    private const int MaxSteps = 40;

    [Fact]
    public void MatchesReferenceModel()
    {
        Gen<(int Kind, int A, int B)[]> genSteps = Gen.Select(Gen.Int[0, 6], Gen.Int[0, 200], Gen.Int[0, 7]).Array[1, MaxSteps];

        // Start at 1, or just before the wrap, so runs cross uint.MaxValue -> 0 (a clamp tick, which these young ticks
        // pass unchanged).
        var genStart = Gen.OneOf(Gen.Const(1u), Gen.UInt[uint.MaxValue - 60, uint.MaxValue]);
        Gen.Select(genSteps, genStart).Sample((steps, start) => Run(steps, start), iter: 200, threads: 1);
    }

    private static void Run((int Kind, int A, int B)[] steps, uint start)
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            world.SetChangeTickForTesting(start);
            List<Entity> entities = [world.Spawn(new Payload(0), new Health(0))];
            int capacity = EnumerationSupport.ArchetypeOf(world, entities[0]).Layout.Capacity;
            Model model = new(start, capacity);
            model.Spawn(1);
            Query[] queries =
            [
                world.Query().Write<Payload>().Changed<Payload>().Build(),
                world.Query().Read<Payload>().Added<Health>().Build(),
                world.Query().Changed<Payload>().Changed<Health>().Build(),
                world.Query().Write<Payload>().Read<Health>().Build(),
            ];
            try
            {
                foreach (Query query in queries)
                {
                    model.AddQuery(world.ChangeTick);
                }

                foreach ((int kind, int a, int b) in steps)
                {
                    switch (kind)
                    {
                        case 0:
                            for (int i = 0; i <= a % 70; i++)
                            {
                                entities.Add(world.Spawn(new Payload(i), new Health(i)));
                            }

                            model.Spawn((a % 70) + 1);
                            break;
                        case 1:
                            world.Write<Payload>(entities[a % entities.Count]).Values[0]++;
                            model.Write(a % entities.Count, health: false);
                            break;
                        case 2:
                            world.Write<Health>(entities[a % entities.Count]).Value++;
                            model.Write(a % entities.Count, health: true);
                            break;
                        case 3:
                            world.AdvanceChangeTick();
                            model.Tick++;
                            break;
                        default:
                            // Kinds 4 to 6: enumerate query b % 4, ending after (a % 4) + 1 chunks when kind is 5, and
                            // writing Payload in the loop on chunks whose index bit b is set when the query writes it.
                            Enumerate(world, queries[b % 4], b % 4, model, breakAfter: kind == 5 ? (a % 4) + 1 : int.MaxValue, writeMask: a);
                            break;
                    }

                    Assert.Equal(model.Tick, world.ChangeTick);
                    for (int q = 0; q < queries.Length; q++)
                    {
                        Assert.Equal(model.HasRun[q], queries[q].HasRun);
                        Assert.Equal(model.LastRun[q], queries[q].LastRunTick);
                    }
                }
            }
            finally
            {
                foreach (Query query in queries)
                {
                    query.Dispose();
                }
            }
        }

        tracking.ThrowIfLeaks();
    }

    private static void Enumerate(EcsWorld world, Query query, int q, Model model, int breakAfter, int writeMask)
    {
        List<int> expected = model.Matching(q);
        int yielded = 0;
        foreach (ChunkView chunk in query)
        {
            int index = EnumerationSupport.ChunkOf(world, chunk.Entities[0]);
            Assert.Equal(expected[yielded], index);
            Assert.Equal(model.RowsIn(index), chunk.Count);
            Assert.Equal(model.IsNewer(q, model.ChangedPayload[index]), chunk.Changed<Payload>());
            if (q != 0 && q != 2)
            {
                Assert.Equal(model.IsNewer(q, model.AddedHealth[index]), chunk.Added<Health>());
            }

            if ((q == 0 || q == 3) && (writeMask & (1 << (index % 8))) != 0)
            {
                _ = chunk.Write<Payload>();
                model.ChangedPayload[index] = model.Tick;
            }

            if (++yielded == breakAfter)
            {
                break;
            }
        }

        // An enumeration that ended early yielded what it got to and changed nothing; a complete one ran its course.
        if (yielded < breakAfter)
        {
            Assert.Equal(expected.Count, yielded);
            model.LastRun[q] = model.Tick;
            model.HasRun[q] = true;
            model.Tick++;
        }
    }

    private sealed class Model(uint tick, int capacity)
    {
        public uint Tick { get; set; } = tick;

        public int Rows { get; private set; }

        public List<uint> ChangedPayload { get; } = [];

        public List<uint> ChangedHealth { get; } = [];

        public List<uint> AddedHealth { get; } = [];

        public List<uint> LastRun { get; } = [];

        public List<bool> HasRun { get; } = [];

        public void AddQuery(uint tick)
        {
            LastRun.Add(tick);
            HasRun.Add(false);
        }

        // A spawn stamps the Changed and Added ticks of every column of the chunk its row lands in.
        public void Spawn(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int chunk = Rows / capacity;
                while (ChangedPayload.Count <= chunk)
                {
                    ChangedPayload.Add(Tick);
                    ChangedHealth.Add(Tick);
                    AddedHealth.Add(Tick);
                }

                ChangedPayload[chunk] = Tick;
                ChangedHealth[chunk] = Tick;
                AddedHealth[chunk] = Tick;
                Rows++;
            }
        }

        public void Write(int row, bool health)
        {
            List<uint> ticks = health ? ChangedHealth : ChangedPayload;
            ticks[row / capacity] = Tick;
        }

        public int RowsIn(int chunk) => System.Math.Min(capacity, Rows - (chunk * capacity));

        public bool IsNewer(int q, uint stamp) => !HasRun[q] || unchecked((int)(stamp - LastRun[q])) > 0;

        // The chunks query q yields, by its filters: 0 Changed<Payload>, 1 Added<Health>, 2 both Changed, 3 none.
        public List<int> Matching(int q)
        {
            List<int> chunks = [];
            for (int chunk = 0; chunk < ChangedPayload.Count; chunk++)
            {
                bool passes = q switch
                {
                    0 => IsNewer(q, ChangedPayload[chunk]),
                    1 => IsNewer(q, AddedHealth[chunk]),
                    2 => IsNewer(q, ChangedPayload[chunk]) && IsNewer(q, ChangedHealth[chunk]),
                    _ => true,
                };
                if (passes)
                {
                    chunks.Add(chunk);
                }
            }

            return chunks;
        }
    }
}
