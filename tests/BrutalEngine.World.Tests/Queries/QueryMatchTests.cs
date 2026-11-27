using System;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Queries;
using BrutalEngine.World.Storage;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// Query terms and matching (docs/design/world.md, section 5, "Matching"): an archetype matches when its mask holds
/// every required term and no excluded one, over both words of a 128-bit mask.
/// </summary>
public sealed class QueryMatchTests
{
    // The fixture's archetypes, in creation order after the empty archetype (index 0). Ids 2 and 65 are tags; 63/64
    // and 127 sit on the mask's word boundaries.
    private static readonly int[][] _fixtureArchetypes =
    [
        [0],          // 1
        [0, 1],       // 2
        [1, 2],       // 3
        [0, 64],      // 4
        [64],         // 5
        [0, 1, 64],   // 6
        [2, 65],      // 7
        [0, 127],     // 8
        [63],         // 9
    ];

    [Theory]
    [InlineData("", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 })]
    [InlineData("R0", new[] { 1, 2, 4, 6, 8 })]
    [InlineData("W1", new[] { 2, 3, 6 })]
    [InlineData("H2", new[] { 3, 7 })]
    [InlineData("X1", new[] { 0, 1, 4, 5, 7, 8, 9 })]
    [InlineData("C1", new[] { 2, 3, 6 })]
    [InlineData("A64", new[] { 4, 5, 6 })]
    [InlineData("R64", new[] { 4, 5, 6 })]
    [InlineData("R63", new[] { 9 })]
    [InlineData("R127", new[] { 8 })]
    [InlineData("H65", new[] { 7 })]
    [InlineData("X64", new[] { 0, 1, 2, 3, 7, 8, 9 })]
    [InlineData("R0 X64", new[] { 1, 2, 8 })]
    [InlineData("X127 R0", new[] { 1, 2, 4, 6 })]
    [InlineData("R0 W1 X64", new[] { 2 })]
    [InlineData("R0 H1 R64", new[] { 6 })]
    [InlineData("r1", new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 })]
    [InlineData("w64 R0", new[] { 1, 2, 4, 6, 8 })]
    [InlineData("H65 X2", new int[0])]
    [InlineData("WC1", new[] { 2, 3, 6 })]
    [InlineData("RA0 X1", new[] { 1, 4, 8 })]
    [InlineData("RX0", new int[0])]
    [InlineData("R3", new int[0])]
    [InlineData("X0 X1 X64", new[] { 0, 7, 9 })]
    public void Terms_MatchTable(string spec, int[] expected)
    {
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, id => id is 2 or 65))
        {
            foreach (int[] ids in _fixtureArchetypes)
            {
                _ = QueryTestSupport.Archetype(world, ids);
            }

            Assert.Equal(_fixtureArchetypes.Length + 1, world.ArchetypeCount);
            QueryTerm[] parsed = QueryTestSupport.ParseTerms(spec);
            var terms = QueryTerms.Create(parsed, QueryTestSupport.MaskBits);

            // The terms give the expected answer for every archetype, and so does the set-based model.
            for (int index = 0; index < world.ArchetypeCount; index++)
            {
                ArchetypeSignature signature = world.Archetypes[index].Signature;
                bool expectedMatch = Array.IndexOf(expected, index) >= 0;
                Assert.Equal(expectedMatch, terms.Matches(signature));
                Assert.Equal(expectedMatch, QueryTestSupport.ModelMatches(signature.Ids.ToArray(), parsed));
            }
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData((int)TermFlags.Read, false, true)]
    [InlineData((int)TermFlags.Write, false, true)]
    [InlineData((int)TermFlags.With, false, true)]
    [InlineData((int)TermFlags.Without, true, false)]
    [InlineData((int)TermFlags.OptionalRead, true, true)]
    [InlineData((int)TermFlags.OptionalWrite, true, true)]
    [InlineData((int)TermFlags.Changed, false, true)]
    [InlineData((int)TermFlags.Added, false, true)]
    public void Matches_EachFlag_RequiredExcludedOrOptional(int flagValue, bool matchesWithout, bool matchesWith)
    {
        // An int parameter: the flags are internal, and a public test method can't take them.
        var flag = (TermFlags)flagValue;
        TrackingAllocator tracking = QueryTestSupport.NewTracking();
        using (EcsWorld world = QueryTestSupport.NewWorld(tracking, _ => false))
        {
            Archetype without = QueryTestSupport.Archetype(world, 1);
            Archetype with = QueryTestSupport.Archetype(world, 1, 70);
            var terms = QueryTerms.Create([new QueryTerm(70, flag)], QueryTestSupport.MaskBits);

            Assert.Equal(matchesWithout, terms.Matches(without.Signature));
            Assert.Equal(matchesWith, terms.Matches(with.Signature));
            Assert.Equal(QueryTestSupport.IsDataTerm(flag) ? 1 : 0, terms.DataTermCount);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Matches_EveryFlagValue_IsCoveredByTheFlagTable()
    {
        // Matches_EachFlag_RequiredExcludedOrOptional has one row per flag; a new flag needs a row there.
        Assert.Equal(
            [TermFlags.None, TermFlags.Read, TermFlags.Write, TermFlags.With, TermFlags.Without, TermFlags.OptionalRead, TermFlags.OptionalWrite, TermFlags.Changed, TermFlags.Added],
            Enum.GetValues<TermFlags>());
    }

    [Fact]
    public void Create_KeepsTermsAndDataTermsInGivenOrder()
    {
        QueryTerm[] input = QueryTestSupport.ParseTerms("H1 R5 X2 C3 w4 WC0 r127");
        var terms = QueryTerms.Create(input, QueryTestSupport.MaskBits);

        Assert.Equal(7, terms.Count);
        Assert.Equal(2, terms.MaskWords);
        for (int i = 0; i < input.Length; i++)
        {
            Assert.Equal(input[i], terms[i]);
        }

        // With and Without alone have no column access; every other term is a data term, in the given order.
        Assert.Equal(5, terms.DataTermCount);
        int[] expected = [5, 3, 4, 0, 127];
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], terms.GetDataTerm(i).Component);
        }

        Assert.Equal(TermFlags.Write | TermFlags.Changed, terms.GetDataTerm(3).Flags);

        // The input is copied: changing it afterwards changes nothing.
        input[1] = new QueryTerm(9, TermFlags.Without);
        Assert.Equal(new QueryTerm(5, TermFlags.Read), terms[1]);
        Assert.Equal(5, terms.GetDataTerm(0).Component);
    }

    [Fact]
    public void Create_NoTerms_MatchesEverything()
    {
        var terms = QueryTerms.Create([], 64);

        Assert.Equal(0, terms.Count);
        Assert.Equal(0, terms.DataTermCount);
        Assert.Equal(1, terms.MaskWords);
        Assert.True(terms.Matches(ArchetypeSignature.Create([0, 5, 63], 64)));
        Assert.True(terms.Matches(ArchetypeSignature.Create([], 64)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-64)]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(int.MinValue)]
    public void Create_InvalidMaskBits_Throws(int maskBits)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(() => QueryTerms.Create([], maskBits));
        Assert.Equal("maskBits", ex.ParamName);
    }

    [Theory]
    [InlineData(64, 1)]
    [InlineData(128, 2)]
    [InlineData(4096, 64)]
    public void Create_ValidMaskBits_SetsMaskWords(int maskBits, int words)
    {
        Assert.Equal(words, QueryTerms.Create([], maskBits).MaskWords);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(QueryTestSupport.MaskBits)]
    [InlineData(int.MaxValue)]
    public void Create_ComponentOutOfRange_Throws(int component)
    {
        ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => QueryTerms.Create([new QueryTerm(1, TermFlags.Read), new QueryTerm(component, TermFlags.Read)], QueryTestSupport.MaskBits));
        Assert.Equal("terms", ex.ParamName);
        Assert.Contains("Term 1", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(QueryTestSupport.MaskBits - 1)]
    public void Create_ComponentAtRangeEnds_IsRequired(int component)
    {
        var terms = QueryTerms.Create([new QueryTerm(component, TermFlags.Read)], QueryTestSupport.MaskBits);

        Assert.True(terms.Matches(ArchetypeSignature.Create([component], QueryTestSupport.MaskBits)));
        Assert.False(terms.Matches(ArchetypeSignature.Create([], QueryTestSupport.MaskBits)));
    }

    [Fact]
    public void Create_TermWithoutFlags_Throws()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => QueryTerms.Create([new QueryTerm(3, TermFlags.Read), new QueryTerm(4, TermFlags.None)], QueryTestSupport.MaskBits));
        Assert.Equal("terms", ex.ParamName);
        Assert.Contains("Term 1 for component 4", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("R5 W5")]
    [InlineData("R5 H7 X5")]
    [InlineData("R70 H7 C70")]
    public void Create_RepeatedComponent_Throws(string spec)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => QueryTerms.Create(QueryTestSupport.ParseTerms(spec), QueryTestSupport.MaskBits));
        Assert.Equal("terms", ex.ParamName);
        Assert.Contains("repeats", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_SameBitInAnotherWord_IsNotARepeat()
    {
        // 6 and 70 share bit 6 of their words.
        var terms = QueryTerms.Create(QueryTestSupport.ParseTerms("R6 X70"), QueryTestSupport.MaskBits);

        Assert.True(terms.Matches(ArchetypeSignature.Create([6], QueryTestSupport.MaskBits)));
        Assert.False(terms.Matches(ArchetypeSignature.Create([6, 70], QueryTestSupport.MaskBits)));
    }

    [Theory]
    [InlineData(64)]
    [InlineData(256)]
    public void Matches_SignatureOfAnotherWidth_Throws(int signatureBits)
    {
        var terms = QueryTerms.Create([], QueryTestSupport.MaskBits);

        ArgumentException ex = Assert.Throws<ArgumentException>(() => terms.Matches(ArchetypeSignature.Create([1], signatureBits)));
        Assert.Equal("signature", ex.ParamName);
    }
}
