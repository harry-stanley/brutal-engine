using System;
using System.Collections.Generic;
using System.Globalization;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Internal;
using BrutalEngine.World.Queries;
using BrutalEngine.World.Storage;
using Xunit;

namespace BrutalEngine.World.Tests.Queries;

/// <summary>
/// Shared helpers for the query matching tests: worlds whose registry is filled with made-up components (some of them
/// tags), terms written as short strings, and a set-based reference model of matching.
/// </summary>
internal static class QueryTestSupport
{
    /// <summary>The mask width of the test worlds: two 64-bit words, so word-boundary ids (63, 64, 127) exist.</summary>
    public const int MaskBits = 128;

    /// <summary>A fresh tracking allocator per test, sized so its bookkeeping never grows inside a measured window.</summary>
    public static TrackingAllocator NewTracking() => new(new HeapAllocator(), "Queries", initialCapacity: 1024);

    /// <summary>
    /// A world over <paramref name="allocator"/> with <see cref="MaskBits"/>-bit masks, all of whose component ids are
    /// registered: id <c>i</c> is a tag when <paramref name="isTag"/> says so, otherwise data of <c>4 + 4 * (i % 4)</c>
    /// bytes, so column offsets differ between archetypes.
    /// </summary>
    public static EcsWorld NewWorld(INativeAllocator allocator, Func<int, bool> isTag)
    {
        EcsWorld world = new(new WorldOptions
        {
            Allocator = allocator,
            ChunkBytes = WorldOptions.SmallestChunkBytes,
            MaxComponentTypes = MaskBits,
        });
        ComponentRegistry registry = world.Components;
        for (int id = 0; id < MaskBits; id++)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"Tests.Query.C{id}");
            bool tag = isTag(id);
            ComponentInfo info = new()
            {
                StableId = StableComponentId.FromFullName(name),
                SchemaVersion = 1,
                FullName = name,
                IsTag = tag,
            };
            _ = registry.Add(null, ComponentOrdinal.Reserve(), in info, tag ? 1 : 4 + (4 * (id % 4)), tag ? 1 : 4);
        }

        return world;
    }

    /// <summary>Creates (or finds) the archetype of exactly <paramref name="ids"/>.</summary>
    public static Archetype Archetype(EcsWorld world, params int[] ids)
    {
        var components = new ComponentId[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            components[i] = new ComponentId(ids[i]);
        }

        return world.Archetypes.GetOrCreate(components);
    }

    /// <summary>
    /// Parses terms written as space-separated tokens: flag letters, then the component id. <c>R</c> Read, <c>W</c>
    /// Write, <c>H</c> With, <c>X</c> Without, <c>r</c> OptionalRead, <c>w</c> OptionalWrite, <c>C</c> Changed,
    /// <c>A</c> Added; <c>"WC5"</c> is Write and Changed on component 5.
    /// </summary>
    public static QueryTerm[] ParseTerms(string spec)
    {
        List<QueryTerm> terms = [];
        foreach (string token in spec.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int digits = token.AsSpan().IndexOfAnyInRange('0', '9');
            TermFlags flags = TermFlags.None;
            foreach (char letter in token.AsSpan(0, digits))
            {
                flags |= letter switch
                {
                    'R' => TermFlags.Read,
                    'W' => TermFlags.Write,
                    'H' => TermFlags.With,
                    'X' => TermFlags.Without,
                    'r' => TermFlags.OptionalRead,
                    'w' => TermFlags.OptionalWrite,
                    'C' => TermFlags.Changed,
                    'A' => TermFlags.Added,
                    _ => throw new ArgumentException($"Unknown term letter '{letter}' in '{token}'.", nameof(spec)),
                };
            }

            terms.Add(new QueryTerm(int.Parse(token.AsSpan(digits), CultureInfo.InvariantCulture), flags));
        }

        return [.. terms];
    }

    /// <summary>
    /// The reference model of matching, written over sets rather than masks: every component of a term with a required
    /// flag is in the archetype, and no component of a term with <see cref="TermFlags.Without"/> is.
    /// </summary>
    public static bool ModelMatches(IReadOnlyCollection<int> archetypeIds, IEnumerable<QueryTerm> terms)
    {
        const TermFlags Required = TermFlags.Read | TermFlags.Write | TermFlags.With | TermFlags.Changed | TermFlags.Added;
        HashSet<int> present = [.. archetypeIds];
        foreach (QueryTerm term in terms)
        {
            if ((term.Flags & Required) != 0 && !present.Contains(term.Component))
            {
                return false;
            }

            if ((term.Flags & TermFlags.Without) != 0 && present.Contains(term.Component))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether a term reads or writes its component's column: the model of a data term.</summary>
    public static bool IsDataTerm(TermFlags flags) =>
        (flags & (TermFlags.Read | TermFlags.Write | TermFlags.OptionalRead | TermFlags.OptionalWrite | TermFlags.Changed | TermFlags.Added)) != 0;

    /// <summary>The indices of the archetypes a cache holds, in entry order.</summary>
    public static int[] MatchedIndices(ArchetypeMatchCache cache)
    {
        int[] indices = new int[cache.Count];
        for (int i = 0; i < indices.Length; i++)
        {
            indices[i] = cache.GetArchetype(i).Index;
        }

        return indices;
    }

    /// <summary>
    /// Checks every entry's column and offset per data term against the archetype's layout, found by searching the
    /// layout's columns for the component (not through the signature's type indices, as the cache does).
    /// </summary>
    public static void AssertColumns(ArchetypeMatchCache cache, QueryTerms terms)
    {
        for (int entry = 0; entry < cache.Count; entry++)
        {
            Archetype archetype = cache.GetArchetype(entry);
            ReadOnlySpan<ComponentType> columns = archetype.Layout.Columns;
            for (int term = 0; term < terms.DataTermCount; term++)
            {
                int expected = -1;
                for (int column = 0; column < columns.Length; column++)
                {
                    if (columns[column].Id.Index == terms.GetDataTerm(term).Component)
                    {
                        expected = column;
                    }
                }

                Assert.Equal(expected, cache.GetColumn(entry, term));
                Assert.Equal(expected < 0 ? -1 : archetype.Layout.ColumnOffsets[expected], cache.GetOffset(entry, term));
            }
        }
    }
}
