using System;

namespace BrutalEngine.World.Storage;

/// <summary>
/// The component set of an archetype: its component ids, sorted and distinct, plus a bitmask with one bit per id
/// (docs/design/world.md, section 3, "Masks").
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity.</b> Two signatures are equal when they hold the same ids, so the order and repetition of the ids an
/// archetype was asked for never matter: <see cref="Normalize"/> sorts and deduplicates them first. The hash
/// (<see cref="ComputeHash"/>) is a fixed function of the sorted ids, the same in every process, and is used only to
/// find a signature, never to order anything. The mask's width isn't part of the identity either: signatures with the
/// same ids are equal even if they were created with different mask widths.
/// </para>
/// <para>
/// <b>Mask.</b> <see cref="Mask"/> is <c>MaxComponentTypes</c> bits wide, one <see cref="ulong"/> per 64 ids; bit
/// <c>i</c> is set exactly when id <c>i</c> is in the signature, so a query matches an archetype with one AND per
/// word.
/// </para>
/// <para>
/// Immutable after construction and safe to read from any thread. <see cref="Create"/> allocates the id and mask
/// arrays (once per archetype); no other member allocates.
/// </para>
/// </remarks>
internal sealed class ArchetypeSignature : IEquatable<ArchetypeSignature>
{
    private readonly int[] _ids;
    private readonly ulong[] _mask;

    private ArchetypeSignature(int[] ids, ulong[] mask, int hash)
    {
        _ids = ids;
        _mask = mask;
        Hash = hash;
    }

    /// <summary>The number of components.</summary>
    public int Count => _ids.Length;

    /// <summary>The component ids' indices, in strictly ascending order.</summary>
    public ReadOnlySpan<int> Ids => _ids;

    /// <summary>The mask: bit <c>i % 64</c> of word <c>i / 64</c> is set exactly when id <c>i</c> is present.</summary>
    public ReadOnlySpan<ulong> Mask => _mask;

    /// <summary>The hash of <see cref="Ids"/>: <see cref="ComputeHash"/> of them.</summary>
    public int Hash { get; }

    /// <summary>Returns whether the signature holds the component with index <paramref name="id"/>.</summary>
    /// <param name="id">Any index; one outside the mask's width is never present.</param>
    /// <returns><see langword="true"/> if <paramref name="id"/> is one of <see cref="Ids"/>.</returns>
    public bool Contains(int id) => (uint)id < (uint)(_mask.Length * 64) && (_mask[id >> 6] & (1UL << id)) != 0;

    /// <summary>Returns whether the signature holds exactly <paramref name="sortedIds"/>.</summary>
    /// <param name="sortedIds">Ids in strictly ascending order, as <see cref="Normalize"/> leaves them.</param>
    /// <returns><see langword="true"/> if <see cref="Ids"/> equals <paramref name="sortedIds"/> element by element.</returns>
    public bool Matches(ReadOnlySpan<int> sortedIds) => sortedIds.SequenceEqual(_ids);

    /// <summary>Creates a signature from ids that are already sorted and distinct.</summary>
    /// <param name="sortedIds">The ids, in strictly ascending order, each from 0 to <paramref name="maskBits"/> - 1. Copied.</param>
    /// <param name="maskBits">The mask's width: the world's <c>MaxComponentTypes</c>, a positive multiple of 64.</param>
    /// <returns>The signature. Allocates its id and mask arrays.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maskBits"/> isn't a positive multiple of 64, or an id is negative or at least <paramref name="maskBits"/>.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="sortedIds"/> isn't strictly ascending.</exception>
    public static ArchetypeSignature Create(ReadOnlySpan<int> sortedIds, int maskBits)
    {
        if (maskBits <= 0 || maskBits % 64 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maskBits), maskBits, "The mask width must be a positive multiple of 64.");
        }

        ulong[] mask = new ulong[maskBits / 64];
        for (int i = 0; i < sortedIds.Length; i++)
        {
            int id = sortedIds[i];
            if ((uint)id >= (uint)maskBits)
            {
                throw new ArgumentOutOfRangeException(nameof(sortedIds), id, $"Component ids must be from 0 to {maskBits - 1}.");
            }

            if (i > 0 && id <= sortedIds[i - 1])
            {
                throw new ArgumentException(
                    $"Component ids must be in strictly ascending order; index {i} has id {id} after {sortedIds[i - 1]}.",
                    nameof(sortedIds));
            }

            mask[id >> 6] |= 1UL << id;
        }

        return new ArchetypeSignature(sortedIds.ToArray(), mask, ComputeHash(sortedIds));
    }

    /// <summary>Sorts <paramref name="ids"/> in place and removes repeated ids, keeping one of each.</summary>
    /// <param name="ids">Any ids, in any order, with repeats.</param>
    /// <returns>The number of distinct ids, now at the start of <paramref name="ids"/> in ascending order.</returns>
    /// <remarks>Allocates nothing.</remarks>
    public static int Normalize(Span<int> ids)
    {
        if (ids.IsEmpty)
        {
            return 0;
        }

        ids.Sort();
        int count = 1;
        for (int i = 1; i < ids.Length; i++)
        {
            if (ids[i] != ids[count - 1])
            {
                ids[count++] = ids[i];
            }
        }

        return count;
    }

    /// <summary>Hashes sorted ids with a fixed function, so every process and run gets the same value.</summary>
    /// <param name="sortedIds">Ids in strictly ascending order.</param>
    /// <returns>The hash: 32-bit FNV-1a over the ids (one step per id), then the MurmurHash3 finaliser, so the low bits that pick a slot depend on every id.</returns>
    /// <remarks>Allocates nothing.</remarks>
    public static int ComputeHash(ReadOnlySpan<int> sortedIds)
    {
        uint hash = 2166136261u;
        foreach (int id in sortedIds)
        {
            hash = (hash ^ (uint)id) * 16777619u;
        }

        hash ^= hash >> 16;
        hash *= 0x85EBCA6Bu;
        hash ^= hash >> 13;
        hash *= 0xC2B2AE35u;
        hash ^= hash >> 16;
        return (int)hash;
    }

    /// <summary>Compares the ids of two signatures.</summary>
    /// <param name="other">The signature to compare with.</param>
    /// <returns><see langword="true"/> if both hold the same ids, whatever their mask widths.</returns>
    public bool Equals(ArchetypeSignature? other) =>
        other is not null && (ReferenceEquals(this, other) || (Hash == other.Hash && Matches(other._ids)));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ArchetypeSignature);

    /// <inheritdoc />
    public override int GetHashCode() => Hash;
}
