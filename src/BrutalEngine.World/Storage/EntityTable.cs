using System;
using BrutalEngine.Core.Memory;

namespace BrutalEngine.World.Storage;

/// <summary>
/// A world's entity table: one 16-byte <see cref="EntityRecord"/> per slot, indexed by <see cref="Entity.Index"/>, in
/// an unmanaged array (docs/design/world.md, section 2, "Entity table").
/// </summary>
/// <remarks>
/// <para>
/// <b>Slots.</b> Fresh slots are issued in index order at generation 1. A freed slot advances its generation and
/// joins a first-in, first-out queue (<see cref="FreeIndexRing"/>), and freed slots are reused before fresh ones, so
/// generations advance as slowly as possible. A slot freed at generation <see cref="uint.MaxValue"/> is retired
/// instead: it is never reused, so a stale entity can never become current again. Live, pending and retired slots
/// together never exceed <see cref="MaxEntities"/>.
/// </para>
/// <para>
/// <b>States.</b> <see cref="Allocate"/> takes a slot and marks it pending; the direct call that allocated it places
/// it with <see cref="Place"/> before it returns, so that pending state is never observable outside the call.
/// <see cref="Place"/> also moves a placed entity to another archetype, <see cref="Move"/> changes its chunk and row
/// within its archetype, and <see cref="Free"/> releases a placed slot.
/// </para>
/// <para>
/// <b>Memory.</b> The records and the free-index ring are two blocks from the allocator given to the constructor,
/// which the table doesn't own. Both grow by doubling, capped at <see cref="MaxEntities"/>, when a fresh slot is
/// needed and the records are full; growth frees the old blocks, and leaves the table unchanged if an allocation
/// fails. <see cref="Dispose"/> frees both blocks once. No member allocates managed memory, except to throw.
/// </para>
/// <para>
/// Not thread-safe: the owning world's owner thread is its only caller. Index arguments name slots, not entities;
/// callers that hold an <see cref="Entity"/> check it against the slot's generation first.
/// </para>
/// </remarks>
internal sealed unsafe class EntityTable : IDisposable
{
    // Records are 16 bytes; a 16-byte aligned block keeps every record inside one 64-byte cache line.
    private const nuint RecordAlignment = 16;

    private readonly INativeAllocator _allocator;
    private readonly FreeIndexRing _free;
    private EntityRecord* _records;
    private int _capacity;
    private int _slotCount;
    private int _placedCount;
    private int _retiredCount;

    /// <summary>Creates an empty table with <paramref name="initialCapacity"/> slots reserved.</summary>
    /// <param name="allocator">Source of the table's blocks. Not owned; must outlive the table.</param>
    /// <param name="initialCapacity">Slots reserved up front, from 1 to <paramref name="maxEntities"/>.</param>
    /// <param name="maxEntities">Most slots the table ever issues: live, pending and retired together. At least 1.</param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count is outside its range.</exception>
    /// <exception cref="OutOfMemoryException">The allocator can't provide the blocks. Nothing stays allocated.</exception>
    public EntityTable(INativeAllocator allocator, int initialCapacity, int maxEntities)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxEntities, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialCapacity, maxEntities);

        _allocator = allocator;
        MaxEntities = maxEntities;
        _free = new FreeIndexRing(allocator, initialCapacity);
        try
        {
            _records = AllocateRecords(allocator, initialCapacity);
        }
        catch
        {
            _free.Dispose();
            throw;
        }

        _capacity = initialCapacity;
    }

    /// <summary>Most slots the table ever issues: live, pending and retired together (<c>WorldOptions.MaxEntities</c>).</summary>
    public int MaxEntities { get; }

    /// <summary>Slots the records block currently holds. <c>0</c> once disposed.</summary>
    public int Capacity => _capacity;

    /// <summary>Slots issued so far, in any state; valid indices are <c>0</c> to <c>SlotCount - 1</c>.</summary>
    public int SlotCount => _slotCount;

    /// <summary>Slots whose entity is placed in storage (alive).</summary>
    public int PlacedCount => _placedCount;

    /// <summary>Freed slots waiting in the queue for reuse.</summary>
    public int FreeCount => _free.Count;

    /// <summary>Slots retired at generation <see cref="uint.MaxValue"/>, never to be reused.</summary>
    public int RetiredCount => _retiredCount;

    /// <summary>
    /// Takes the longest-free slot, or a fresh one if none is free, and marks it pending. The caller places it with
    /// <see cref="Place"/> before returning to its own caller.
    /// </summary>
    /// <returns>The entity for the slot, at the slot's current generation.</returns>
    /// <exception cref="InvalidOperationException">
    /// No slot is free and all <see cref="MaxEntities"/> slots are live, pending or retired. The message names
    /// <c>WorldOptions.MaxEntities</c>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    /// <exception cref="OutOfMemoryException">Growing the table failed. The table is unchanged.</exception>
    public Entity Allocate()
    {
        ThrowIfDisposed();
        if (!_free.TryDequeue(out int index))
        {
            if (_slotCount == MaxEntities)
            {
                throw new InvalidOperationException(
                    $"The world has no entity slot left: all {MaxEntities} slots allowed by WorldOptions.MaxEntities are live, pending or retired.");
            }

            if (_slotCount == _capacity)
            {
                Grow();
            }

            index = _slotCount;

            // Sound: index < _capacity after the check and growth above, and _records holds _capacity records.
            _records[index].Generation = 1;
            _slotCount++;
        }

        // Sound: index < _slotCount <= _capacity, from the queue or the fresh slot above.
        ref EntityRecord record = ref _records[index];
        record.Archetype = EntityRecord.PendingState;
        record.Chunk = -1;
        record.Row = -1;
        return new Entity((uint)index, record.Generation);
    }

    /// <summary>
    /// Records where a pending slot's entity was stored, making it alive, or where a placed entity moved to in
    /// another archetype.
    /// </summary>
    /// <param name="index">The slot. Must be pending or placed.</param>
    /// <param name="archetype">The archetype now holding the entity. Not negative.</param>
    /// <param name="chunk">The chunk within the archetype. Not negative.</param>
    /// <param name="row">The row within the chunk. Not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> isn't an issued slot, or a location is negative.</exception>
    /// <exception cref="InvalidOperationException">The slot is free or retired. Nothing changes.</exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    public void Place(int index, int archetype, int chunk, int row)
    {
        ThrowIfDisposed();
        ref EntityRecord record = ref RecordAt(index);
        ArgumentOutOfRangeException.ThrowIfNegative(archetype);
        ArgumentOutOfRangeException.ThrowIfNegative(chunk);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        if (record.Archetype is EntityRecord.FreeState or EntityRecord.RetiredState)
        {
            throw new InvalidOperationException($"Entity slot {index} is not in use, so it can't be placed.");
        }

        if (record.Archetype == EntityRecord.PendingState)
        {
            _placedCount++;
        }

        record.Archetype = archetype;
        record.Chunk = chunk;
        record.Row = row;
    }

    /// <summary>Records that a placed entity moved to another chunk or row of its archetype (a swap-remove).</summary>
    /// <param name="index">The slot. Must be placed.</param>
    /// <param name="chunk">The new chunk. Not negative.</param>
    /// <param name="row">The new row. Not negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> isn't an issued slot, or a location is negative.</exception>
    /// <exception cref="InvalidOperationException">The slot isn't placed. Nothing changes.</exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    public void Move(int index, int chunk, int row)
    {
        ThrowIfDisposed();
        ref EntityRecord record = ref RecordAt(index);
        ArgumentOutOfRangeException.ThrowIfNegative(chunk);
        ArgumentOutOfRangeException.ThrowIfNegative(row);
        ThrowIfNotPlaced(ref record, index);

        record.Chunk = chunk;
        record.Row = row;
    }

    /// <summary>
    /// Releases a placed slot: its generation advances and it joins the back of the reuse queue, or, at generation
    /// <see cref="uint.MaxValue"/>, it is retired. Either way every copy of its entity becomes stale.
    /// </summary>
    /// <param name="index">The slot. Must be placed.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> isn't an issued slot.</exception>
    /// <exception cref="InvalidOperationException">The slot isn't placed. Nothing changes.</exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    public void Free(int index)
    {
        ThrowIfDisposed();
        ref EntityRecord record = ref RecordAt(index);
        ThrowIfNotPlaced(ref record, index);

        _placedCount--;
        record.Chunk = -1;
        record.Row = -1;
        if (record.Generation == uint.MaxValue)
        {
            // Advancing would wrap to 0 (reserved for Entity.Null) and then reissue old generations. Retire instead.
            record.Archetype = EntityRecord.RetiredState;
            _retiredCount++;
            return;
        }

        record.Generation++;
        record.Archetype = EntityRecord.FreeState;

        // Never full: the ring holds at least _capacity indices, and fewer than _slotCount slots are free.
        _free.Enqueue(index);
    }

    /// <summary>Returns a copy of a slot's record.</summary>
    /// <param name="index">The slot.</param>
    /// <returns>The record.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> isn't an issued slot.</exception>
    /// <exception cref="ObjectDisposedException">The table is disposed.</exception>
    public EntityRecord GetRecord(int index)
    {
        ThrowIfDisposed();
        return RecordAt(index);
    }

    /// <summary>Test seam: overrides a slot's generation, so retirement at <see cref="uint.MaxValue"/> can be tested.</summary>
    internal void SetGenerationForTesting(int index, uint generation)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfZero(generation);
        RecordAt(index).Generation = generation;
    }

    /// <summary>Frees the records and the free-index ring. Idempotent; afterwards other members throw <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        if (_records == null)
        {
            return;
        }

        _allocator.Free(_records);
        _records = null;
        _free.Dispose();
        _capacity = 0;
        _slotCount = 0;
        _placedCount = 0;
        _retiredCount = 0;
    }

    private static EntityRecord* AllocateRecords(INativeAllocator allocator, int capacity) =>
        (EntityRecord*)allocator.Allocate(checked((nuint)(uint)capacity * (nuint)sizeof(EntityRecord)), RecordAlignment);

    private static void ThrowIfNotPlaced(ref EntityRecord record, int index)
    {
        if (record.Archetype < 0)
        {
            throw new InvalidOperationException($"Entity slot {index} is not placed.");
        }
    }

    private ref EntityRecord RecordAt(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _slotCount);

        // Sound: 0 <= index < _slotCount <= _capacity, and _records holds _capacity records.
        return ref _records[index];
    }

    private void Grow()
    {
        int capacity = (int)Math.Min(2L * _capacity, MaxEntities);

        // The ring first: if the records allocation then fails, the ring is merely larger than it needs to be, and
        // the invariant that it can hold every slot's index still holds.
        if (_free.Capacity < capacity)
        {
            _free.Grow(capacity);
        }

        EntityRecord* records = AllocateRecords(_allocator, capacity);

        // Sound: both blocks hold at least _slotCount records (capacity > _capacity >= _slotCount), and they are
        // separate allocations, so they don't overlap.
        new ReadOnlySpan<EntityRecord>(_records, _slotCount).CopyTo(new Span<EntityRecord>(records, _slotCount));
        _allocator.Free(_records);
        _records = records;
        _capacity = capacity;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_records == null, this);
}
