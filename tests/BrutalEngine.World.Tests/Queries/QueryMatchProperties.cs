using System;
using System.Collections.Generic;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Queries;
using BrutalEngine.World.Storage;
using CsCheck;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// Random archetype sets and terms against a set-based model of matching (docs/design/world.md, section 5): the cache
/// holds exactly the matching archetypes in creation order, picks up archetypes created after it, examines each
/// archetype once, and records every data term's column.
/// </summary>
public sealed class QueryMatchProperties
{
    // Component ids drawn by the generators: low ids, both sides of the 64-bit word boundary and the last id, so a
    // mutant that checks only one mask word fails. 2 and 65 are tags (no column).
    private static readonly int[] _pool = [0, 1, 2, 3, 63, 64, 65, 126, 127];

    // Each term's flags: every single flag, and the combinations the builder allows. Without appears twice, so
    // excluded terms are common.
    private static readonly TermFlags[] _flagChoices =
    [
        TermFlags.Read, TermFlags.Write, TermFlags.With, TermFlags.Without, TermFlags.Without,
        TermFlags.OptionalRead, TermFlags.OptionalWrite, TermFlags.Changed, TermFlags.Added,
        TermFlags.Write | TermFlags.Changed, TermFlags.Read | TermFlags.Added, TermFlags.With | TermFlags.Changed,
    ];

    [Fact]
    public void MatchesReference()
    {
        Gen<int[][]> genArchetypes = Gen.Int[0, _pool.Length - 1].Array[0, 6].Array[0, 40];
        Gen<(int, int)[]> genTerms = Gen.Select(Gen.Int[0, _pool.Length - 1], Gen.Int[0, _flagChoices.Length - 1]).Array[0, 5];

        Gen.Select(genArchetypes, genTerms, Gen.Int[0, 40], Gen.Int[1, 6]).Sample((archetypes, termPicks, buildAfter, refreshEvery) =>
        {
            // One term per component: a later pick of the same component is dropped.
            List<QueryTerm> terms = [];
            HashSet<int> named = [];
            foreach ((int component, int flags) in termPicks)
            {
                if (named.Add(_pool[component]))
                {
                    terms.Add(new QueryTerm(_pool[component], _flagChoices[flags]));
                }
            }

            TrackingAllocator tracking = QueryTestSupport.NewTracking();
            using (EcsWorld world = QueryTestSupport.NewWorld(tracking, id => id is 2 or 65))
            {
                ArchetypeSet set = world.Archetypes;
                var queryTerms = QueryTerms.Create([.. terms], QueryTestSupport.MaskBits);
                ArchetypeMatchCache? cache = null;
                try
                {
                    for (int step = 0; step <= archetypes.Length; step++)
                    {
                        if (step == Math.Min(buildAfter, archetypes.Length))
                        {
                            cache = new ArchetypeMatchCache(set, queryTerms);
                        }

                        if (cache is not null && (step % refreshEvery == 0 || step == archetypes.Length))
                        {
                            cache.Refresh();
                            Check(set, cache, queryTerms, terms);
                        }

                        if (step < archetypes.Length)
                        {
                            int[] ids = new int[archetypes[step].Length];
                            for (int i = 0; i < ids.Length; i++)
                            {
                                ids[i] = _pool[archetypes[step][i]];
                            }

                            _ = QueryTestSupport.Archetype(world, ids);
                        }
                    }
                }
                finally
                {
                    cache?.Dispose();
                }
            }

            tracking.ThrowIfLeaks();
        }, iter: 300, threads: 1);
    }

    private static void Check(ArchetypeSet set, ArchetypeMatchCache cache, QueryTerms queryTerms, List<QueryTerm> terms)
    {
        List<int> expected = [];
        for (int index = 0; index < set.Count; index++)
        {
            if (QueryTestSupport.ModelMatches(set[index].Signature.Ids.ToArray(), terms))
            {
                expected.Add(index);
            }
        }

        Assert.Equal(expected, QueryTestSupport.MatchedIndices(cache));

        // Every archetype has been examined exactly once, however many refreshes there were.
        Assert.Equal(set.Count, cache.ScannedArchetypes);
        Assert.Equal(set.Count, cache.ArchetypesExamined);

        int dataTerms = 0;
        foreach (QueryTerm term in terms)
        {
            dataTerms += QueryTestSupport.IsDataTerm(term.Flags) ? 1 : 0;
        }

        Assert.Equal(dataTerms, queryTerms.DataTermCount);
        QueryTestSupport.AssertColumns(cache, queryTerms);
    }
}
