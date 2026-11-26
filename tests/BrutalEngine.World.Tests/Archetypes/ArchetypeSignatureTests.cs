using System;
using System.Linq;
using BrutalEngine.World.Storage;
using CsCheck;
using Xunit;

namespace BrutalEngine.World.Tests.Archetypes;

public sealed class ArchetypeSignatureTests
{
    // Two mask words, with ids on both sides of the word boundary at 64.
    private const int MaskBits = 128;

    [Fact]
    public void OrderIndependent()
    {
        // A component set, then the same set repeated and shuffled: ids near the word boundary and at both ends of the
        // mask are drawn often.
        var genId = Gen.OneOf(Gen.Int[0, MaskBits - 1], Gen.Int[60, 67], Gen.Const(0), Gen.Const(MaskBits - 1));
        Gen.Select(genId.Array[0, 12], Gen.Int[0, 3], Gen.UInt).Sample((ids, repeats, seed) =>
        {
            int[] distinct = [.. ids.Distinct().Order()];
            int[] variant = Shuffle([.. ids, .. ids.Take(repeats)], seed);

            var direct = ArchetypeSignature.Create(distinct, MaskBits);
            var normalized = ArchetypeSignature.Create(variant.AsSpan(0, ArchetypeSignature.Normalize(variant)), MaskBits);

            // The same signature, holding the sorted distinct ids, and the mask has exactly their bits.
            Assert.Equal(direct, normalized);
            Assert.Equal(direct.Hash, normalized.Hash);
            Assert.Equal(distinct, normalized.Ids.ToArray());
            ulong[] mask = new ulong[MaskBits / 64];
            foreach (int id in distinct)
            {
                mask[id / 64] |= 1UL << (id % 64);
            }

            Assert.Equal(mask, normalized.Mask.ToArray());
            for (int id = -1; id <= MaskBits; id++)
            {
                Assert.Equal(distinct.Contains(id), normalized.Contains(id));
            }
        }, iter: 1000);
    }

    [Theory]
    [InlineData(new int[0], new int[0])]
    [InlineData(new[] { 7 }, new[] { 7 })]
    [InlineData(new[] { 5, 1, 5, 3, 1 }, new[] { 1, 3, 5 })]
    [InlineData(new[] { 2, 2, 2 }, new[] { 2 })]
    [InlineData(new[] { 64, 63, 0, 127 }, new[] { 0, 63, 64, 127 })]
    public void Normalize_SortsAndRemovesRepeats(int[] ids, int[] expected)
    {
        int count = ArchetypeSignature.Normalize(ids);

        Assert.Equal(expected, ids[..count]);
    }

    [Theory]
    [InlineData(new[] { 2, 1 })]
    [InlineData(new[] { 1, 1 })]
    public void Create_NotStrictlyAscending_Throws(int[] ids) =>
        Assert.Throws<ArgumentException>(() => ArchetypeSignature.Create(ids, 64));

    [Theory]
    [InlineData(-1, 64)]
    [InlineData(64, 64)]
    [InlineData(int.MinValue, 128)]
    public void Create_IdOutsideMask_Throws(int id, int maskBits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ArchetypeSignature.Create([0, id], maskBits));

    [Theory]
    [InlineData(0)]
    [InlineData(-64)]
    [InlineData(65)]
    public void Create_MaskBitsNotMultipleOf64_Throws(int maskBits) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ArchetypeSignature.Create([0], maskBits));

    [Fact]
    public void Create_CopiesIds()
    {
        int[] ids = [1, 2];
        var signature = ArchetypeSignature.Create(ids, 64);
        ids[0] = 0;

        Assert.Equal([1, 2], signature.Ids.ToArray());
    }

    [Fact]
    public void Equality_ComparesIds()
    {
        var a = ArchetypeSignature.Create([1, 2], 64);
        var same = ArchetypeSignature.Create([1, 2], 128);
        var other = ArchetypeSignature.Create([1, 3], 64);

        Assert.True(a.Equals(same));
        Assert.True(a.Equals((object)same));
        Assert.Equal(a.GetHashCode(), same.GetHashCode());
        Assert.False(a.Equals(other));
        Assert.False(a.Equals(ArchetypeSignature.Create([1], 64)));
        Assert.False(a.Equals(null));
        Assert.True(a.Matches([1, 2]));
        Assert.False(a.Matches([1, 2, 3]));
    }

    [Fact]
    public void Equality_HashCollision_ComparesIds()
    {
        // Two sets with the same hash, found by a birthday search with a separate Python implementation of the hash
        // (934726784 for both), so only the ids can tell them apart.
        var first = ArchetypeSignature.Create([163, 215, 1190, 1580, 2107, 2340], 4096);
        var second = ArchetypeSignature.Create([811, 1099, 2434, 3274, 4069], 4096);

        Assert.Equal(934726784, first.Hash);
        Assert.Equal(first.Hash, second.Hash);
        Assert.False(first.Equals(second));
        Assert.False(second.Equals((object)first));
    }

    // Expected values from a separate Python implementation of the same definition: per id, hash = (hash ^ id) *
    // 16777619 mod 2^32 from 2166136261, then the MurmurHash3 fmix32 finaliser, read as a signed 32-bit int.
    [Theory]
    [InlineData(new int[0], -1421968373)]
    [InlineData(new[] { 0 }, 91794203)]
    [InlineData(new[] { 1, 2, 3 }, 1315153499)]
    [InlineData(new[] { 5, 64, 4095 }, 674419680)]
    public void ComputeHash_KnownVectors(int[] ids, int expected)
    {
        Assert.Equal(expected, ArchetypeSignature.ComputeHash(ids));
        Assert.Equal(expected, ArchetypeSignature.Create(ids, 4096).Hash);
    }

    // Fisher-Yates with a xorshift generator, so a sample's order is a function of its seed.
    private static int[] Shuffle(int[] ids, uint seed)
    {
        uint state = seed | 1;
        for (int i = ids.Length - 1; i > 0; i--)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            int j = (int)(state % (uint)(i + 1));
            (ids[i], ids[j]) = (ids[j], ids[i]);
        }

        return ids;
    }
}
