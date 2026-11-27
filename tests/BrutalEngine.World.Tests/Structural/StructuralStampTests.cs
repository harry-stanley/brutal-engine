using BrutalEngine.Core.Memory;
using BrutalEngine.World.Storage;
using BrutalEngine.World.Tests.Entities;
using Xunit;
using static BrutalEngine.World.Tests.Structural.StructuralSupport;

namespace BrutalEngine.World.Tests.Structural;

/// <summary>
/// The change ticks a move stamps (docs/design/world.md, section 4, "Moves"): in the target chunk, <c>Changed</c> for
/// every column and <c>Added</c> for the added component; in the source chunk that received the moved last row,
/// <c>Changed</c> for every column; nothing in any other chunk. Every check covers every chunk of the archetypes
/// involved, on archetypes of two or three chunks, so a stamp in the wrong chunk or column fails.
/// </summary>
public sealed class StructuralStampTests
{
    [Fact]
    public void Add_MoveRules()
    {
        TrackingAllocator tracking = EntityTestSupport.NewTracking();
        using (EcsWorld world = EntityTestSupport.NewWorld(tracking))
        {
            // Columns: S = {Health, Payload} has [Health, Payload]; T = S + Position has [Position, Health, Payload];
            // TM = T + Marker has the same three columns (the tag has none).
            RegisterInOrder(world);

            // On a world whose tick was never set, a move stamps tick 1: the target's new chunk, and the source chunk
            // that received the moved row.
            Entity zero = world.Spawn(new Health(-1), new Payload(-1));
            Entity zeroNext = world.Spawn(new Health(-2), new Payload(-2));
            world.Add(zero, new Position(-1, -1));
            Archetype s = ArchetypeOf(world, zeroNext);
            Archetype t = ArchetypeOf(world, zero);
            AssertChunks(s, ([1, 1], [1, 1]));
            AssertChunks(t, ([1, 1, 1], [1, 1, 1]));
            world.Despawn(zeroNext);

            // S: a full chunk 0 and four rows in chunk 1, at tick 2. T: a full chunk 0 and two rows in chunk 1, at 3.
            world.SetChangeTickForTesting(2);
            var e = new Entity[s.Layout.Capacity + 4];
            for (int i = 0; i < e.Length; i++)
            {
                e[i] = world.Spawn(new Health(i), new Payload(i));
            }

            world.SetChangeTickForTesting(3);
            for (int i = 0; i < t.Layout.Capacity + 1; i++)
            {
                _ = world.Spawn(new Position(i, i), new Health(i), new Payload(i));
            }

            int cap = s.Layout.Capacity;
            AssertChunks(s, ([2, 2], [2, 2]), ([2, 2], [2, 2]));
            AssertChunks(t, ([3, 3, 3], [3, 3, 3]), ([3, 3, 3], [3, 3, 3]));

            // A row of S's chunk 0: the target's last chunk (1) gets Changed everywhere and Added for Position (column
            // 0); S's chunk 0 received S's last row and gets Changed everywhere. Nothing else moves.
            world.SetChangeTickForTesting(7);
            world.Add(e[5], new Position(5, 5));
            Assert.Equal((t.Index, 1, 2), Where(world, e[5]));
            Assert.Equal((s.Index, 0, 5), Where(world, e[cap + 3]));
            AssertChunks(s, ([7, 7], [2, 2]), ([2, 2], [2, 2]));
            AssertChunks(t, ([3, 3, 3], [3, 3, 3]), ([7, 7, 7], [7, 3, 3]));

            // A row of S's chunk 1 that isn't S's last: chunk 1 receives the moved row, chunk 0 keeps its ticks.
            world.SetChangeTickForTesting(8);
            world.Add(e[cap], new Position(cap, cap));
            Assert.Equal((s.Index, 1, 0), Where(world, e[cap + 2]));
            AssertChunks(s, ([7, 7], [2, 2]), ([8, 8], [2, 2]));
            AssertChunks(t, ([3, 3, 3], [3, 3, 3]), ([8, 8, 8], [8, 3, 3]));

            // S's last row: nothing moves in S, so S's chunks keep their ticks; chunk 1 keeps a row, so its ticks are
            // still there to check.
            world.SetChangeTickForTesting(9);
            world.Add(e[cap + 1], new Position(1, 1));
            Assert.Equal(1, s.Chunks.GetCount(1));
            AssertChunks(s, ([7, 7], [2, 2]), ([8, 8], [2, 2]));
            AssertChunks(t, ([3, 3, 3], [3, 3, 3]), ([9, 9, 9], [9, 3, 3]));

            // A full target chunk: the move opens T's chunk 2, which starts at the current tick, and T's chunk 1 keeps
            // the ticks the spawns that filled it stamped.
            world.SetChangeTickForTesting(10);
            while (t.Chunks.GetCount(1) < t.Layout.Capacity)
            {
                _ = world.Spawn(new Position(0, 0), new Health(0), new Payload(0));
            }

            world.SetChangeTickForTesting(11);
            world.Add(e[0], new Position(0, 0));
            Assert.Equal((t.Index, 2, 0), Where(world, e[0]));
            Assert.Equal((s.Index, 0, 0), Where(world, e[cap + 2]));
            AssertChunks(s, ([11, 11], [2, 2]));
            AssertChunks(t, ([3, 3, 3], [3, 3, 3]), ([10, 10, 10], [10, 10, 10]), ([11, 11, 11], [11, 11, 11]));

            // A tag: the target chunk gets Changed everywhere and no Added tick, as the tag has no column. T's last row
            // (the only row of chunk 2) fills the hole in chunk 0, and chunk 2 goes back to the pool.
            world.SetChangeTickForTesting(12);
            Entity tagged = world.Spawn(new Position(0, 0), new Health(0), new Payload(0), default(Marker));
            Archetype tm = ArchetypeOf(world, tagged);
            world.SetChangeTickForTesting(13);
            world.Add(zero, default(Marker));
            Assert.Equal((tm.Index, 0, 1), Where(world, zero));
            Assert.Equal((t.Index, 0, 0), Where(world, e[0]));
            AssertChunks(tm, ([13, 13, 13], [12, 12, 12]));
            AssertChunks(t, ([13, 13, 13], [3, 3, 3]), ([10, 10, 10], [10, 10, 10]));
            AssertChunks(s, ([11, 11], [2, 2]));

            // A component in the middle of the target's columns: the Added tick lands in Health's column (1), into
            // TM's existing chunk, and the other columns keep theirs.
            world.SetChangeTickForTesting(14);
            Entity noHealth = world.Spawn(new Position(0, 0), new Payload(0), default(Marker));
            world.SetChangeTickForTesting(15);
            world.Add(noHealth, new Health(15));
            Assert.Equal((tm.Index, 0, 2), Where(world, noHealth));
            AssertChunks(tm, ([15, 15, 15], [12, 15, 12]));
            AssertChunks(t, ([13, 13, 13], [3, 3, 3]), ([10, 10, 10], [10, 10, 10]));
        }

        tracking.ThrowIfLeaks();
    }

    private static (int Archetype, int Chunk, int Row) Where(EcsWorld world, Entity entity)
    {
        EntityRecord record = Location(world, entity);
        return (record.Archetype, record.Chunk, record.Row);
    }

    /// <summary>Checks every chunk of <paramref name="archetype"/>: one (changed, added) pair per chunk, in order.</summary>
    private static void AssertChunks(Archetype archetype, params (uint[] Changed, uint[] Added)[] chunks)
    {
        Assert.Equal(chunks.Length, archetype.Chunks.ChunkCount);
        for (int c = 0; c < chunks.Length; c++)
        {
            Assert.Equal(chunks[c].Changed, archetype.Chunks.GetChanged(c).ToArray());
            Assert.Equal(chunks[c].Added, archetype.Chunks.GetAdded(c).ToArray());
        }
    }
}
