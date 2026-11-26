using System;
using BrutalEngine.Core.Memory;

namespace BrutalEngine.World.Storage;

/// <summary>
/// An archetype's chunk list and its change metadata: per chunk, the block and its row count, and per chunk and column
/// a <c>Changed</c> and an <c>Added</c> tick (docs/design/world.md, section 4, "Change metadata lives outside the
/// chunk").
/// </summary>
/// <remarks>
/// <para>
/// <b>Chunks.</b> <see cref="AddChunk"/> rents a block from the world's <see cref="ChunkPool"/> and appends it with no
/// rows; <see cref="RemoveLastChunk"/> returns an emptied last chunk's block to the pool. The archetype keeps the
/// dense invariant (every chunk but the last is full) and each chunk's row count through <see cref="SetCount"/>; the
/// table only stores the count.
/// </para>
/// <para>
/// <b>Ticks.</b> Each chunk has one <c>Changed</c> and one <c>Added</c> tick per column, in two arrays of chunk x
/// column ticks, chunk-major, so a chunk's ticks are contiguous (<see cref="GetChanged"/>, <see cref="GetAdded"/>).
/// A new chunk's ticks all start at the tick it was added at. A filtered query reads these compact arrays instead of
/// touching each block.
/// </para>
/// <para>
/// <b>Memory.</b> The entry array and the two tick arrays come from the world's allocator, which the table doesn't
/// own; an archetype without columns has no tick arrays. The constructor allocates nothing native. The arrays grow
/// together by doubling, all or nothing: every new array is allocated before anything is copied, so a failed
/// allocation leaves the table unchanged. <see cref="Dispose"/> returns every block to the pool, which must not be
/// disposed yet, and frees the arrays. No member allocates managed memory, except to throw. Array sizes use checked
/// arithmetic, so an impossible size throws <see cref="OverflowException"/> instead of wrapping to a short array.
/// </para>
/// <para>
/// Not thread-safe: the owning world's owner thread is its only caller. Spans and pointers from the accessors are
/// valid until the next <see cref="AddChunk"/>, <see cref="RemoveLastChunk"/> or <see cref="Dispose"/>.
/// </para>
/// </remarks>
internal sealed unsafe class ChunkTable : IDisposable
{
    /// <summary>The capacity of the arrays after the first growth, in chunks.</summary>
    public const int InitialCapacity = 4;

    // Entries are 16 bytes on 64-bit targets; ticks are 4 bytes. 16-byte alignment keeps an entry in one cache line.
    private const nuint ArrayAlignment = 16;

    private readonly INativeAllocator _allocator;
    private readonly ChunkPool _pool;
    private ChunkEntry* _entries;
    private uint* _changed;
    private uint* _added;
    private int _capacity;
    private int _count;
    private bool _disposed;

    /// <summary>Creates an empty table. Allocates no native memory: the arrays are allocated by the first <see cref="AddChunk"/>.</summary>
    /// <param name="allocator">Source of the entry and tick arrays: the world's allocator. Not owned; must outlive the table.</param>
    /// <param name="pool">Source of the chunk blocks: the world's pool. Not owned; must outlive the table.</param>
    /// <param name="columnCount">The archetype's columns (its non-tag components), from 0 to 4,096.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> or <paramref name="pool"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="columnCount"/> is outside its range.</exception>
    public ChunkTable(INativeAllocator allocator, ChunkPool pool, int columnCount)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentNullException.ThrowIfNull(pool);
        ArgumentOutOfRangeException.ThrowIfNegative(columnCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(columnCount, WorldOptions.LargestMaxComponentTypes);
        _allocator = allocator;
        _pool = pool;
        ColumnCount = columnCount;
    }

    /// <summary>The number of columns, and so of <c>Changed</c> and <c>Added</c> ticks per chunk.</summary>
    public int ColumnCount { get; }

    /// <summary>The number of chunks. <c>0</c> once disposed.</summary>
    public int ChunkCount => _count;

    /// <summary>The number of chunks the arrays hold before they grow. <c>0</c> before the first chunk and once disposed.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Appends a chunk with no rows: rents a block from the pool, and grows the arrays if they are full. Every column's
    /// <c>Changed</c> and <c>Added</c> tick in the new chunk is <paramref name="tick"/>.
    /// </summary>
    /// <param name="tick">The world's current change tick.</param>
    /// <returns>The new chunk's index: the previous <see cref="ChunkCount"/>.</returns>
    /// <remarks>
    /// The block comes from the pool, which asks the allocator only when it holds none; the arrays are reallocated
    /// only when full. If growing fails after the block was rented, the block goes back to the pool, so a later call
    /// reuses it. Growth swaps the new arrays in before it frees the old ones: if the allocator's free throws, the
    /// exception propagates with the table already on the grown arrays and every chunk kept, but no chunk added (the
    /// block goes back to the pool); the old arrays not yet freed leak.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    /// <exception cref="OutOfMemoryException">The allocator can't provide the block or the grown arrays. The table is unchanged.</exception>
    public int AddChunk(uint tick)
    {
        ThrowIfDisposed();
        byte* block = _pool.Rent();
        if (_count == _capacity)
        {
            try
            {
                Grow();
            }
            catch
            {
                _pool.Return(block);
                throw;
            }
        }

        int index = _count;

        // Sound: index < _capacity after the growth above, and the entry array holds _capacity entries.
        _entries[index] = new ChunkEntry(block);
        if (ColumnCount > 0)
        {
            // Sound: both tick arrays hold _capacity * ColumnCount ticks, so the chunk's row of ticks is inside them.
            nint offset = (nint)index * ColumnCount;
            new Span<uint>(_changed + offset, ColumnCount).Fill(tick);
            new Span<uint>(_added + offset, ColumnCount).Fill(tick);
        }

        _count++;
        return index;
    }

    /// <summary>Removes the last chunk, which must be empty, and returns its block to the pool. Makes no allocator call.</summary>
    /// <exception cref="InvalidOperationException">There is no chunk, or the last chunk still has rows. Nothing changes.</exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    public void RemoveLastChunk()
    {
        ThrowIfDisposed();
        if (_count == 0)
        {
            throw new InvalidOperationException("The chunk table has no chunk to remove.");
        }

        // Sound: _count - 1 is the last chunk, inside the entry array.
        ChunkEntry* last = _entries + (_count - 1);
        if (last->Count != 0)
        {
            throw new InvalidOperationException(
                $"The last chunk still has {last->Count} rows; only an emptied chunk can be removed.");
        }

        _pool.Return(last->Data);
        *last = default;
        _count--;
    }

    /// <summary>Returns a chunk's block: <see cref="ChunkPool.ChunkBytes"/> bytes, 64-byte aligned.</summary>
    /// <param name="chunk">The chunk index, from 0 to <see cref="ChunkCount"/> - 1.</param>
    /// <returns>The block. Its contents beyond the rows the archetype wrote are uninitialised.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunk"/> isn't a chunk index.</exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    public byte* GetData(int chunk) => EntryAt(chunk)->Data;

    /// <summary>Returns a chunk's row count.</summary>
    /// <param name="chunk">The chunk index, from 0 to <see cref="ChunkCount"/> - 1.</param>
    /// <returns>The rows in use, <c>0</c> for a new chunk.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunk"/> isn't a chunk index.</exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    public int GetCount(int chunk) => EntryAt(chunk)->Count;

    /// <summary>Sets a chunk's row count. The archetype keeps it within its layout's capacity.</summary>
    /// <param name="chunk">The chunk index, from 0 to <see cref="ChunkCount"/> - 1.</param>
    /// <param name="count">The rows in use. Not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunk"/> isn't a chunk index, or <paramref name="count"/> is negative.</exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    public void SetCount(int chunk, int count)
    {
        ChunkEntry* entry = EntryAt(chunk);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        entry->Count = count;
    }

    /// <summary>Returns a chunk's <c>Changed</c> ticks, one per column in column order.</summary>
    /// <param name="chunk">The chunk index, from 0 to <see cref="ChunkCount"/> - 1.</param>
    /// <returns>The ticks, writable; empty when the archetype has no columns.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunk"/> isn't a chunk index.</exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    public Span<uint> GetChanged(int chunk) => TicksAt(_changed, chunk);

    /// <summary>Returns a chunk's <c>Added</c> ticks, one per column in column order.</summary>
    /// <param name="chunk">The chunk index, from 0 to <see cref="ChunkCount"/> - 1.</param>
    /// <returns>The ticks, writable; empty when the archetype has no columns.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunk"/> isn't a chunk index.</exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    public Span<uint> GetAdded(int chunk) => TicksAt(_added, chunk);

    /// <summary>
    /// Returns every chunk's block to the pool, whatever its row count, and frees the arrays. Idempotent; afterwards
    /// other members throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <remarks>
    /// The pool must not be disposed yet. If it is, the return throws <see cref="ObjectDisposedException"/>; the
    /// arrays are still freed and the table is still disposed, but the blocks stay allocated (a tracking allocator
    /// reports them as leaks). If the allocator's free throws, the exception propagates and the table is still
    /// disposed, holding no array; the arrays not yet freed leak, and a later Dispose frees nothing again.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The pool was disposed before the table.</exception>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        // Detach everything before any call that can throw, so no freed array stays reachable from the table.
        _disposed = true;
        ChunkEntry* entries = _entries;
        uint* changed = _changed;
        uint* added = _added;
        int count = _count;
        _entries = null;
        _changed = null;
        _added = null;
        _count = 0;
        _capacity = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                // Sound: i < count, the chunk count when disposal began, and the entry array held that many entries.
                _pool.Return(entries[i].Data);
            }
        }
        finally
        {
            FreeArrays(entries, changed, added);
        }
    }

    // Checked: an impossible size throws OverflowException rather than wrapping to a short array.
    private static nuint EntryBytes(int capacity) => checked((nuint)(uint)capacity * (nuint)sizeof(ChunkEntry));

    private nuint TickBytes(int capacity) => checked((nuint)(uint)capacity * (nuint)(uint)ColumnCount * sizeof(uint));

    private void Grow()
    {
        int capacity = _capacity == 0 ? InitialCapacity : checked(_capacity * 2);

        // Every new array first, so a failure frees only the new ones and leaves the table as it was.
        var entries = (ChunkEntry*)_allocator.Allocate(EntryBytes(capacity), ArrayAlignment);
        uint* changed = null;
        uint* added = null;
        if (ColumnCount > 0)
        {
            try
            {
                changed = (uint*)_allocator.Allocate(TickBytes(capacity), ArrayAlignment);
                added = (uint*)_allocator.Allocate(TickBytes(capacity), ArrayAlignment);
            }
            catch
            {
                FreeArrays(entries, changed, null);
                throw;
            }
        }

        if (_count > 0)
        {
            // Sound: the old arrays hold _count chunks (_count == _capacity < capacity), the new ones hold capacity,
            // and they are separate allocations, so the copies stay in bounds and don't overlap.
            new ReadOnlySpan<ChunkEntry>(_entries, _count).CopyTo(new Span<ChunkEntry>(entries, _count));
            if (ColumnCount > 0)
            {
                int ticks = checked(_count * ColumnCount);
                new ReadOnlySpan<uint>(_changed, ticks).CopyTo(new Span<uint>(changed, ticks));
                new ReadOnlySpan<uint>(_added, ticks).CopyTo(new Span<uint>(added, ticks));
            }
        }

        // Swap first: if a free of an old array throws, the table is already consistent on the new arrays, and the old
        // ones left unfreed are unreachable, so they leak but are never used or freed twice.
        ChunkEntry* oldEntries = _entries;
        uint* oldChanged = _changed;
        uint* oldAdded = _added;
        _entries = entries;
        _changed = changed;
        _added = added;
        _capacity = capacity;
        FreeArrays(oldEntries, oldChanged, oldAdded);
    }

    private void FreeArrays(ChunkEntry* entries, uint* changed, uint* added)
    {
        if (entries != null)
        {
            _allocator.Free(entries);
        }

        if (changed != null)
        {
            _allocator.Free(changed);
        }

        if (added != null)
        {
            _allocator.Free(added);
        }
    }

    private ChunkEntry* EntryAt(int chunk)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(chunk);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(chunk, _count);

        // Sound: 0 <= chunk < _count <= _capacity, and the entry array holds _capacity entries.
        return _entries + chunk;
    }

    private Span<uint> TicksAt(uint* ticks, int chunk)
    {
        _ = EntryAt(chunk);

        // Sound: the chunk index is checked above, and a tick array holds _capacity * ColumnCount ticks; with no
        // columns the array is null and the span is empty, so nothing is read through it.
        return new Span<uint>(ticks + ((nint)chunk * ColumnCount), ColumnCount);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>One chunk: its block and its row count (docs/design/world.md, section 4).</summary>
    private struct ChunkEntry(byte* data)
    {
        /// <summary>The chunk's block, rented from the pool.</summary>
        public byte* Data = data;

        /// <summary>The rows in use.</summary>
        public int Count;
    }
}
