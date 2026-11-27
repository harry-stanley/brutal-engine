using System;
using BrutalEngine.World.Storage;

namespace BrutalEngine.World.Queries;

/// <summary>
/// A query's cache of matching archetypes: for each one, its index and the column and column offset of every data
/// term, in unmanaged memory (docs/design/world.md, section 5, "Caching").
/// </summary>
/// <remarks>
/// <para>
/// <b>Refresh.</b> Archetypes are append-only and never change their components, so an archetype matches a query for
/// the world's whole lifetime or never. <see cref="Refresh"/> therefore examines only the archetypes created since the
/// previous refresh, each exactly once over the cache's lifetime, and appends the ones that match. Entries keep the
/// archetypes' creation order. A new cache has examined nothing: the first refresh examines every archetype.
/// </para>
/// <para>
/// <b>Entries.</b> Entry <c>e</c> holds the archetype index, then per data term <c>t</c> (in
/// <see cref="QueryTerms.GetDataTerm"/> order) the column in the archetype's layout and that column's byte offset in a
/// chunk. Both are <c>-1</c> when the archetype has no column for the term: an optional component it lacks, or a tag.
/// </para>
/// <para>
/// <b>Memory.</b> The entries come from the archetype set's allocator (the world's), which the cache doesn't own. A new
/// cache allocates nothing native; the first matching archetype allocates room for <see cref="InitialCapacity"/>
/// entries, and the array doubles when full, all or nothing. <see cref="Dispose"/> frees it. Constructing a cache
/// allocates the cache object (setup work); <see cref="Refresh"/>, its growth included, the accessors and
/// <see cref="Dispose"/> allocate no managed memory, except to throw: they only read the set and call the allocator.
/// </para>
/// <para>Not thread-safe: the owning world's owner thread is its only caller.</para>
/// </remarks>
internal sealed unsafe class ArchetypeMatchCache : IDisposable
{
    /// <summary>The capacity after the first growth, in entries.</summary>
    public const int InitialCapacity = 8;

    private const nuint EntryAlignment = sizeof(int);

    private readonly ArchetypeSet _set;
    private readonly QueryTerms _terms;
    private readonly int _stride;
    private int* _entries;
    private int _capacity;
    private int _count;
    private int _scanned;
    private bool _disposed;

    /// <summary>Creates an empty cache that has examined no archetype. Allocates no native memory.</summary>
    /// <param name="set">The world's archetypes. Its allocator provides the entries; neither is owned.</param>
    /// <param name="terms">The query's terms, with masks as wide as the set's signatures.</param>
    /// <exception cref="ArgumentNullException"><paramref name="set"/> or <paramref name="terms"/> is <see langword="null"/>.</exception>
    public ArchetypeMatchCache(ArchetypeSet set, QueryTerms terms)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(terms);
        _set = set;
        _terms = terms;
        _stride = 1 + (2 * terms.DataTermCount);
    }

    /// <summary>The number of matched archetypes (entries). <c>0</c> once disposed.</summary>
    public int Count => _count;

    /// <summary>The number of archetypes examined so far: the set's archetype count at the last refresh.</summary>
    public int ScannedArchetypes => _scanned;

    /// <summary>
    /// Test seam: the number of match tests made over the cache's lifetime, one per archetype examined; a test that
    /// throws isn't counted. Equal to <see cref="ScannedArchetypes"/> exactly when no archetype was examined twice. The
    /// engine never reads it.
    /// </summary>
    internal int ArchetypesExamined { get; private set; }

    /// <summary>Examines the archetypes created since the last refresh and appends those that match.</summary>
    /// <exception cref="ObjectDisposedException">The cache is disposed.</exception>
    /// <exception cref="ArgumentException">The set's signatures are wider or narrower than the terms' masks.</exception>
    /// <exception cref="OutOfMemoryException">
    /// The allocator can't grow the entries. The archetypes before the one that needed the room stay matched, and the
    /// next refresh examines that one again.
    /// </exception>
    /// <remarks>
    /// Allocates no managed memory. Takes time proportional to the new archetypes and their data terms; with no new
    /// archetype it does one comparison.
    /// </remarks>
    public void Refresh()
    {
        ThrowIfDisposed();
        int total = _set.Count;
        while (_scanned < total)
        {
            Archetype archetype = _set[_scanned];
            bool matches = _terms.Matches(archetype.Signature);
            ArchetypesExamined++;
            if (matches)
            {
                Append(archetype);
            }

            _scanned++;
        }
    }

    /// <summary>Returns a matched archetype.</summary>
    /// <param name="entry">From 0 to <see cref="Count"/> - 1.</param>
    /// <returns>The archetype, from the set.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entry"/> isn't an entry.</exception>
    /// <exception cref="ObjectDisposedException">The cache is disposed.</exception>
    /// <remarks>Allocates nothing, except to throw.</remarks>
    public Archetype GetArchetype(int entry) => _set[Entry(entry)[0]];

    /// <summary>Returns a data term's column in a matched archetype's layout.</summary>
    /// <param name="entry">From 0 to <see cref="Count"/> - 1.</param>
    /// <param name="dataTerm">From 0 to <see cref="QueryTerms.DataTermCount"/> - 1.</param>
    /// <returns>The column index, or <c>-1</c> if the archetype has no column for the term.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entry"/> or <paramref name="dataTerm"/> is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The cache is disposed.</exception>
    /// <remarks>Allocates nothing, except to throw.</remarks>
    public int GetColumn(int entry, int dataTerm) => Entry(entry)[1 + (2 * CheckDataTerm(dataTerm))];

    /// <summary>Returns the byte offset in a chunk of a data term's column in a matched archetype.</summary>
    /// <param name="entry">From 0 to <see cref="Count"/> - 1.</param>
    /// <param name="dataTerm">From 0 to <see cref="QueryTerms.DataTermCount"/> - 1.</param>
    /// <returns>The offset, or <c>-1</c> if the archetype has no column for the term.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="entry"/> or <paramref name="dataTerm"/> is out of range.</exception>
    /// <exception cref="ObjectDisposedException">The cache is disposed.</exception>
    /// <remarks>Allocates nothing, except to throw.</remarks>
    public int GetOffset(int entry, int dataTerm) => Entry(entry)[2 + (2 * CheckDataTerm(dataTerm))];

    /// <summary>Frees the entries. Idempotent; afterwards the other members throw <see cref="ObjectDisposedException"/>.</summary>
    /// <remarks>If the allocator's free throws, the exception propagates and the cache is still disposed, holding nothing.</remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Detach first, so the freed array is never reachable from the cache.
        _disposed = true;
        int* entries = _entries;
        _entries = null;
        _capacity = 0;
        _count = 0;
        if (entries != null)
        {
            _set.Allocator.Free(entries);
        }
    }

    private void Append(Archetype archetype)
    {
        if (_count == _capacity)
        {
            Grow();
        }

        // Sound: _count < _capacity after the growth, so the entry's _stride ints lie inside the array.
        int* entry = _entries + ((nint)_count * _stride);
        entry[0] = archetype.Index;
        ReadOnlySpan<int> ids = archetype.Signature.Ids;
        for (int term = 0; term < _terms.DataTermCount; term++)
        {
            int typeIndex = ids.BinarySearch(_terms.GetDataTerm(term).Component);
            int column = typeIndex < 0 ? -1 : archetype.Layout.ColumnOf(typeIndex);
            entry[1 + (2 * term)] = column;
            entry[2 + (2 * term)] = column < 0 ? -1 : archetype.Layout.ColumnOffsets[column];
        }

        _count++;
    }

    private void Grow()
    {
        int capacity = _capacity == 0 ? InitialCapacity : checked(_capacity * 2);
        int used = _count * _stride;
        int* entries = (int*)_set.Allocator.Allocate(checked((nuint)(uint)capacity * (nuint)(uint)_stride * sizeof(int)), EntryAlignment);
        if (used > 0)
        {
            // Sound: the old array holds `used` ints, the new one more, and they are separate allocations.
            new ReadOnlySpan<int>(_entries, used).CopyTo(new Span<int>(entries, used));
        }

        // Swap first: if freeing the old array throws, the cache is already consistent on the new one.
        int* old = _entries;
        _entries = entries;
        _capacity = capacity;
        if (old != null)
        {
            _set.Allocator.Free(old);
        }
    }

    private int* Entry(int entry)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(entry);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(entry, _count);

        // Sound: entry < _count <= _capacity.
        return _entries + ((nint)entry * _stride);
    }

    private int CheckDataTerm(int dataTerm)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(dataTerm);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(dataTerm, _terms.DataTermCount);
        return dataTerm;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
