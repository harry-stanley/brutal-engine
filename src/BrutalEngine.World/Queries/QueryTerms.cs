using System;
using BrutalEngine.World.Storage;

namespace BrutalEngine.World.Queries;

/// <summary>
/// What a query declares for one component type: each flag is one builder term (docs/design/world.md, section 5). A
/// type can carry several, such as <see cref="Write"/> with <see cref="Changed"/>.
/// </summary>
[Flags]
internal enum TermFlags : byte
{
    /// <summary>No term: not a valid term on its own.</summary>
    None = 0,

    /// <summary>Required, read access.</summary>
    Read = 1,

    /// <summary>Required, write access.</summary>
    Write = 2,

    /// <summary>Required, no data access (for tags).</summary>
    With = 4,

    /// <summary>Excluded: a matching archetype doesn't have the type.</summary>
    Without = 8,

    /// <summary>Optional, read access.</summary>
    OptionalRead = 16,

    /// <summary>Optional, write access.</summary>
    OptionalWrite = 32,

    /// <summary>Required, read access, and a chunk filter on the type's <c>Changed</c> tick.</summary>
    Changed = 64,

    /// <summary>Required, read access, and a chunk filter on the type's <c>Added</c> tick.</summary>
    Added = 128,
}

/// <summary>One component type's terms in a query: the component's dense id index and every flag declared for it.</summary>
/// <param name="Component">The component's <see cref="ComponentId.Index"/> in the world's registry.</param>
/// <param name="Flags">The terms declared for it, at least one.</param>
internal readonly record struct QueryTerm(int Component, TermFlags Flags);

/// <summary>
/// A query's terms in matching form: a required mask, an excluded mask and the data terms whose columns the query
/// reads or writes (docs/design/world.md, section 5, "Matching").
/// </summary>
/// <remarks>
/// <para>
/// <b>Matching.</b> A term is required when it has any of <see cref="TermFlags.Read"/>, <see cref="TermFlags.Write"/>,
/// <see cref="TermFlags.With"/>, <see cref="TermFlags.Changed"/> and <see cref="TermFlags.Added"/>, and excluded when
/// it has <see cref="TermFlags.Without"/>. An archetype matches when its mask holds every required component and no
/// excluded one: one AND per 64 components. Optional terms don't affect matching. A term that is both required and
/// excluded matches nothing; the query builder rejects that combination before terms reach here.
/// </para>
/// <para>
/// <b>Data terms.</b> The terms with column access (read, write, optional or a change filter), in the order they were
/// given. The match cache records each one's column per matched archetype. <see cref="TermFlags.With"/> and
/// <see cref="TermFlags.Without"/> alone aren't data terms.
/// </para>
/// <para>
/// Immutable after <see cref="Create"/>, which allocates the masks and term arrays (setup work, once per query).
/// <see cref="Matches"/> and <see cref="GetDataTerm"/> allocate nothing (the match cache calls both on every refresh);
/// the other members only read fields. Safe to read from any thread.
/// </para>
/// </remarks>
internal sealed class QueryTerms
{
    private const TermFlags RequiredFlags = TermFlags.Read | TermFlags.Write | TermFlags.With | TermFlags.Changed | TermFlags.Added;

    private const TermFlags DataFlags =
        TermFlags.Read | TermFlags.Write | TermFlags.OptionalRead | TermFlags.OptionalWrite | TermFlags.Changed | TermFlags.Added;

    private readonly ulong[] _required;
    private readonly ulong[] _excluded;
    private readonly QueryTerm[] _terms;
    private readonly int[] _dataTerms;

    private QueryTerms(ulong[] required, ulong[] excluded, QueryTerm[] terms, int[] dataTerms)
    {
        _required = required;
        _excluded = excluded;
        _terms = terms;
        _dataTerms = dataTerms;
    }

    /// <summary>The number of terms: one per component type the query names.</summary>
    public int Count => _terms.Length;

    /// <summary>The number of data terms (see the class remarks).</summary>
    public int DataTermCount => _dataTerms.Length;

    /// <summary>The width of the masks in 64-bit words: the world's <c>MaxComponentTypes</c> / 64.</summary>
    public int MaskWords => _required.Length;

    /// <summary>Returns a term, in the order given to <see cref="Create"/>.</summary>
    /// <param name="index">From 0 to <see cref="Count"/> - 1.</param>
    /// <returns>The term.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public QueryTerm this[int index] => _terms[index];

    /// <summary>Returns a data term, in the order the terms were given to <see cref="Create"/>.</summary>
    /// <param name="dataTerm">From 0 to <see cref="DataTermCount"/> - 1.</param>
    /// <returns>The term.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="dataTerm"/> is out of range.</exception>
    public QueryTerm GetDataTerm(int dataTerm) => _terms[_dataTerms[dataTerm]];

    /// <summary>Builds the matching form of a query's terms.</summary>
    /// <param name="terms">One term per component type, each component at most once. Copied.</param>
    /// <param name="maskBits">The world's <c>MaxComponentTypes</c>: a positive multiple of 64.</param>
    /// <returns>The terms. Allocates the masks and arrays.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maskBits"/> isn't a positive multiple of 64, or a component is negative or at least
    /// <paramref name="maskBits"/>.
    /// </exception>
    /// <exception cref="ArgumentException">A term has no flags, or a component appears twice.</exception>
    public static QueryTerms Create(ReadOnlySpan<QueryTerm> terms, int maskBits)
    {
        if (maskBits <= 0 || maskBits % 64 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maskBits), maskBits, "The mask width must be a positive multiple of 64.");
        }

        ulong[] required = new ulong[maskBits / 64];
        ulong[] excluded = new ulong[maskBits / 64];
        ulong[] seen = new ulong[maskBits / 64];
        int dataTermCount = 0;
        for (int i = 0; i < terms.Length; i++)
        {
            (int id, TermFlags flags) = terms[i];
            if ((uint)id >= (uint)maskBits)
            {
                throw new ArgumentOutOfRangeException(nameof(terms), id, $"Term {i}: component ids must be from 0 to {maskBits - 1}.");
            }

            ulong bit = 1UL << id;
            if (flags == TermFlags.None || (seen[id >> 6] & bit) != 0)
            {
                throw new ArgumentException(
                    $"Term {i} for component {id} has no flags, or repeats an earlier term's component; merge each component's flags into one term.",
                    nameof(terms));
            }

            seen[id >> 6] |= bit;
            if ((flags & RequiredFlags) != 0)
            {
                required[id >> 6] |= bit;
            }

            if ((flags & TermFlags.Without) != 0)
            {
                excluded[id >> 6] |= bit;
            }

            if ((flags & DataFlags) != 0)
            {
                dataTermCount++;
            }
        }

        int[] dataTerms = new int[dataTermCount];
        for (int i = 0, next = 0; i < terms.Length; i++)
        {
            if ((terms[i].Flags & DataFlags) != 0)
            {
                dataTerms[next++] = i;
            }
        }

        return new QueryTerms(required, excluded, terms.ToArray(), dataTerms);
    }

    /// <summary>Returns whether an archetype with <paramref name="signature"/> matches the terms.</summary>
    /// <param name="signature">The archetype's signature, with masks <see cref="MaskWords"/> words wide.</param>
    /// <returns><see langword="true"/> if it has every required component and no excluded one.</returns>
    /// <exception cref="ArgumentException">The signature's mask has another width (another world's archetype).</exception>
    /// <remarks>Allocates nothing, except to throw.</remarks>
    public bool Matches(ArchetypeSignature signature)
    {
        ReadOnlySpan<ulong> mask = signature.Mask;
        if (mask.Length != _required.Length)
        {
            throw new ArgumentException(
                $"The signature's mask is {mask.Length} words wide; the terms' masks are {_required.Length}.", nameof(signature));
        }

        for (int i = 0; i < mask.Length; i++)
        {
            if ((mask[i] & _required[i]) != _required[i] || (mask[i] & _excluded[i]) != 0)
            {
                return false;
            }
        }

        return true;
    }
}
