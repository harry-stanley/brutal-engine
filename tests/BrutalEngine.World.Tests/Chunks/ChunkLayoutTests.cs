using System;
using System.Collections.Generic;
using BrutalEngine.World.Storage;
using CsCheck;
using Xunit;

namespace BrutalEngine.World.Tests.Chunks;

public sealed class ChunkLayoutTests
{
    private static readonly int[] _alignments = [1, 2, 4, 8, 16, 32, 64];

    // A column: size in bytes and alignment. Sizes are multiples of the alignment, as C# struct sizes are.
    private static readonly Gen<(int Size, int Alignment)> _genColumn =
        Gen.Int[0, _alignments.Length - 1].SelectMany(a =>
        {
            int alignment = _alignments[a];
            int maxUnits = ComponentRegistry.MaxComponentSize / alignment;
            return Gen.OneOf(Gen.Const(1), Gen.Const(maxUnits), Gen.Int[1, maxUnits], Gen.Int[1, Math.Min(4, maxUnits)])
                .Select(units => (units * alignment, alignment));
        });

    // An archetype: columns, tags, and an interleaving seed (so tags sit between columns in id order).
    private static readonly Gen<((int Size, int Alignment)[] Columns, int Tags, uint Seed)> _genArchetype =
        Gen.Select(
            Gen.OneOf(_genColumn.Array[0, 4], _genColumn.Array[0, 40], _genColumn.Array[1, 1]),
            Gen.OneOf(Gen.Const(0), Gen.Int[0, 8], Gen.Int[100, 300]),
            Gen.UInt);

    private static readonly int[] _powerOfTwoChunks = [16 * 1024, 32 * 1024, 64 * 1024, 128 * 1024, 256 * 1024, 512 * 1024, 1024 * 1024];

    [Fact]
    public void Layout_MatchesBruteForce()
    {
        // Chunk sizes: the world's powers of two, and sizes built from the archetype itself that the layout of some
        // row count exactly fills, or misses by one byte, so capacity sits exactly on the boundary. Row counts of 16
        // and 17 are drawn often, so the boundary of the row-width rule is hit both ways.
        Gen.Select(_genArchetype, Gen.Int[0, 2], Gen.Int[0, _powerOfTwoChunks.Length - 1], Gen.OneOf(Gen.Const(16), Gen.Const(17), Gen.Int[16, 4000]))
            .Sample((archetype, chunkKind, chunkIndex, rows) =>
            {
                ComponentType[] types = Build(archetype.Columns, archetype.Tags, archetype.Seed);
                int chunkBytes = chunkKind switch
                {
                    0 => _powerOfTwoChunks[chunkIndex],
                    1 => (int)Math.Min(ReferenceEnd(types, rows), WorldOptions.LargestChunkBytes),
                    _ => (int)Math.Min(ReferenceEnd(types, rows) - 1, WorldOptions.LargestChunkBytes),
                };

                int expected = ReferenceCapacity(types, chunkBytes);
                if (expected < ChunkLayout.MinCapacity)
                {
                    Assert.Throws<InvalidOperationException>(() => ChunkLayout.Compute(types, chunkBytes));
                    return;
                }

                var layout = ChunkLayout.Compute(types, chunkBytes);
                Assert.Equal(expected, layout.Capacity);
                AssertLayout(types, layout);
            }, iter: 2000);
    }

    [Fact]
    public void Tags_NoColumn()
    {
        ComponentType[] types =
        [
            Column(0, size: 4, alignment: 4),
            Tag(1),
            Column(2, size: 64, alignment: 64),
            Tag(3),
        ];

        var layout = ChunkLayout.Compute(types, 64 * 1024);

        Assert.Equal(2, layout.ColumnCount);
        Assert.Equal([types[0], types[2]], layout.Columns.ToArray());
        Assert.Equal([0, -1, 1, -1], [layout.ColumnOf(0), layout.ColumnOf(1), layout.ColumnOf(2), layout.ColumnOf(3)]);

        // The tags add nothing: the same layout as the two columns alone.
        var untagged = ChunkLayout.Compute([types[0], types[2]], 64 * 1024);
        Assert.Equal(untagged.Capacity, layout.Capacity);
        Assert.Equal(untagged.ColumnOffsets.ToArray(), layout.ColumnOffsets.ToArray());
    }

    [Fact]
    public void Tags_Only_RowIsEntityId()
    {
        var layout = ChunkLayout.Compute([Tag(0), Tag(1)], 16 * 1024);

        Assert.Equal(0, layout.ColumnCount);
        Assert.Equal(16 * 1024 / ChunkLayout.EntityIdBytes, layout.Capacity);
        Assert.Equal(16 * 1024, layout.UsedBytes);
    }

    [Fact]
    public void TooWide_Rejected()
    {
        // A 1,032-byte row (8-byte id plus 1,024 bytes of columns): 16 rows take 16,512 bytes, past a 16 KiB chunk.
        ComponentType[] types = [Column(0, 992, 8), Column(1, 8, 8), Column(2, 8, 8), Column(3, 8, 8), Column(4, 8, 8)];

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ChunkLayout.Compute(types, 16 * 1024));

        Assert.Contains("[Test.C0, Test.C1, Test.C2, Test.C3, Test.C4]", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1032-byte row", exception.Message, StringComparison.Ordinal);
        Assert.Contains("16 rows take 16512 bytes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("16384-byte chunk", exception.Message, StringComparison.Ordinal);

        // Eight bytes narrower, 16 rows fill the chunk exactly, and the archetype is accepted.
        types[0] = Column(0, 984, 8);
        var layout = ChunkLayout.Compute(types, 16 * 1024);
        Assert.Equal(ChunkLayout.MinCapacity, layout.Capacity);
        Assert.Equal(16 * 1024, layout.UsedBytes);
    }

    [Fact]
    public void TooWide_AlignmentPaddingCounts_Rejected()
    {
        // Unpadded, 16 rows take 16 x (8 + 1 + 960) = 15,504 bytes. Aligned, the 64-aligned column starts at 192, not
        // 144, so they take 15,552: a 15,504-byte chunk holds only 15 rows, and the row is too wide.
        ComponentType[] types = [Column(0, 1, 1), Column(1, 960, 64)];

        _ = Assert.Throws<InvalidOperationException>(() => ChunkLayout.Compute(types, 15_504));

        var layout = ChunkLayout.Compute(types, 15_552);
        Assert.Equal(ChunkLayout.MinCapacity, layout.Capacity);
        Assert.Equal([128, 192], layout.ColumnOffsets.ToArray());
    }

    [Fact]
    public void Compute_UnsortedOrDuplicate_Throws()
    {
        _ = Assert.Throws<ArgumentException>(() => ChunkLayout.Compute([Column(1, 4, 4), Column(0, 4, 4)], 16 * 1024));
        _ = Assert.Throws<ArgumentException>(() => ChunkLayout.Compute([Column(0, 4, 4), Tag(0)], 16 * 1024));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void ColumnOf_IndexOutsideTypes_Throws(int typeIndex)
    {
        // Four types, so 4 is types.Length: one past the last valid index.
        ComponentType[] types = [Column(0, 4, 4), Tag(1), Column(2, 8, 8), Tag(3)];
        var layout = ChunkLayout.Compute(types, 16 * 1024);

        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => layout.ColumnOf(typeIndex));

        Assert.Equal("typeIndex", exception.ParamName);
        Assert.Equal(-1, layout.ColumnOf(types.Length - 1));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1025, 1)]
    [InlineData(8, 3)]
    [InlineData(128, 128)]
    [InlineData(4, 0)]
    public void Compute_SizeOrAlignmentOutsideLimits_Throws(int size, int alignment) =>
        Assert.Throws<ArgumentException>(() => ChunkLayout.Compute([Column(0, size, alignment)], 64 * 1024));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData((1024 * 1024) + 1)]
    public void Compute_ChunkBytesOutOfRange_Throws(int chunkBytes) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ChunkLayout.Compute([Column(0, 4, 4)], chunkBytes));

    // The reference: every row count from 1 upward, each with its own end computation, until one doesn't fit.
    private static int ReferenceCapacity(ComponentType[] types, int chunkBytes)
    {
        int capacity = 0;
        while (ReferenceEnd(types, capacity + 1) <= chunkBytes)
        {
            capacity++;
        }

        return capacity;
    }

    private static long ReferenceEnd(ComponentType[] types, int rows)
    {
        long end = 8L * rows;
        foreach (ComponentType type in types)
        {
            if (type.Info.IsTag)
            {
                continue;
            }

            long alignment = Math.Max(16, type.Alignment);
            long remainder = end % alignment;
            end += (remainder == 0 ? 0 : alignment - remainder) + ((long)rows * type.Size);
        }

        return end;
    }

    // Checks the layout's own offsets: in order, aligned, not overlapping, and within the chunk.
    private static void AssertLayout(ComponentType[] types, ChunkLayout layout)
    {
        long end = 8L * layout.Capacity;
        int column = 0;
        for (int i = 0; i < types.Length; i++)
        {
            if (types[i].Info.IsTag)
            {
                Assert.Equal(-1, layout.ColumnOf(i));
                continue;
            }

            Assert.Equal(column, layout.ColumnOf(i));
            int offset = layout.ColumnOffsets[column];
            Assert.Equal(0, offset % Math.Max(16, types[i].Alignment));
            Assert.InRange(offset, end, end + Math.Max(16, types[i].Alignment) - 1);
            end = offset + ((long)layout.Capacity * types[i].Size);
            column++;
        }

        Assert.Equal(column, layout.ColumnCount);
        Assert.Equal(end, layout.UsedBytes);
        Assert.True(end <= layout.ChunkBytes, $"layout ends at {end}, past the {layout.ChunkBytes}-byte chunk");
    }

    private static ComponentType[] Build((int Size, int Alignment)[] columns, int tags, uint seed)
    {
        // Interleave columns and tags in id order, deterministically from the seed.
        List<ComponentType> types = new(columns.Length + tags);
        int c = 0;
        int t = 0;
        uint state = seed | 1;
        while (c < columns.Length || t < tags)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            bool takeTag = c == columns.Length || (t < tags && (state & 1) == 0);
            types.Add(takeTag ? Tag(types.Count) : Column(types.Count, columns[c].Size, columns[c].Alignment));
            if (takeTag)
            {
                t++;
            }
            else
            {
                c++;
            }
        }

        return [.. types];
    }

    private static ComponentType Column(int id, int size, int alignment) => Make(id, size, alignment, isTag: false);

    private static ComponentType Tag(int id) => Make(id, 1, 1, isTag: true);

    private static ComponentType Make(int id, int size, int alignment, bool isTag)
    {
        string name = $"Test.{(isTag ? "T" : "C")}{id}";
        ComponentInfo info = new()
        {
            StableId = StableComponentId.FromFullName(name),
            SchemaVersion = 1,
            FullName = name,
            IsTag = isTag,
        };
        return new ComponentType(new ComponentId(id), info, size, alignment);
    }
}
