using System;
using System.Runtime.InteropServices;
using BrutalEngine.Core.Memory;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Recording;

/// <summary>
/// Appends command records to a frame's command stream in unmanaged memory (docs/design/graphics.md section 9 and "The
/// frame plan"), in the layout <see cref="CommandRecordHeader"/> describes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Records.</b> Each <c>Append</c> writes one record at the end of <see cref="Written"/>: the header, the payload
/// padded to 8 bytes, the trailing part, and zero bytes up to the next multiple of 8. Every padding byte is written as
/// zero, so the same appends always produce the same bytes. The writer doesn't check that the payload type matches
/// the kind: the encoders choose both, and the reader checks them.
/// </para>
/// <para>
/// <b>Memory.</b> One block from the allocator given to the constructor, which the writer doesn't own. The block grows
/// only when a record doesn't fit, by doubling (as often as the record needs, up to <see cref="MaxCapacity"/>): growth
/// allocates the new block, copies the records written so far, then frees the old block, and leaves the writer
/// unchanged if the allocation fails. <see cref="Reset"/> keeps the block, so a writer kept across frames stops
/// allocating once it has grown to the largest frame. <see cref="Dispose"/> frees the block once.
/// </para>
/// <para>
/// No member allocates managed memory, except to throw. Not thread-safe: one owner (the device's thread, for the frame
/// slot the writer belongs to) calls every member.
/// </para>
/// </remarks>
internal sealed unsafe class CommandWriter : IDisposable
{
    /// <summary>The most bytes a writer holds: <see cref="int.MaxValue"/> rounded down to a multiple of 8.</summary>
    internal const int MaxCapacity = int.MaxValue & ~(CommandRecordHeader.Alignment - 1);

    /// <summary>The capacity a writer starts with when the constructor isn't given one, in bytes.</summary>
    internal const int DefaultInitialCapacity = 4096;

    private readonly INativeAllocator _allocator;
    private byte* _buffer;
    private int _capacity;
    private int _length;

    /// <summary>Creates an empty writer with a block of <paramref name="initialCapacity"/> bytes.</summary>
    /// <param name="allocator">Source of the writer's block. Not owned; must outlive the writer.</param>
    /// <param name="initialCapacity">
    /// The first block's size in bytes: from 8 to <see cref="MaxCapacity"/>, rounded up to a multiple of 8.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="allocator"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="initialCapacity"/> is outside its range.</exception>
    /// <exception cref="OutOfMemoryException">The allocator can't provide the block. Nothing stays allocated.</exception>
    internal CommandWriter(INativeAllocator allocator, int initialCapacity = DefaultInitialCapacity)
    {
        ArgumentNullException.ThrowIfNull(allocator);
        ArgumentOutOfRangeException.ThrowIfLessThan(initialCapacity, CommandRecordHeader.Alignment);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(initialCapacity, MaxCapacity);

        _allocator = allocator;
        _capacity = (int)CommandRecordHeader.AlignUp(initialCapacity);
        _buffer = (byte*)allocator.Allocate((nuint)_capacity, CommandRecordHeader.Alignment);
    }

    /// <summary>The block's size in bytes. 0 once disposed.</summary>
    internal int Capacity => _capacity;

    /// <summary>
    /// The records appended since the writer was created or last reset: a multiple of 8 bytes, starting 8-byte
    /// aligned. Valid until the next <c>Append</c>, <see cref="Reset"/> or <see cref="Dispose"/>.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The writer is disposed.</exception>
    internal ReadOnlySpan<byte> Written
    {
        get
        {
            ThrowIfDisposed();

            // Sound: _buffer holds _capacity bytes and _length <= _capacity; the first _length were written.
            return new ReadOnlySpan<byte>(_buffer, _length);
        }
    }

    /// <summary>Appends a record with no trailing part.</summary>
    /// <typeparam name="T">The kind's payload struct.</typeparam>
    /// <param name="kind">The record's kind: a defined <see cref="CommandKind"/>.</param>
    /// <param name="payload">The payload, copied into the stream.</param>
    /// <exception cref="ObjectDisposedException">The writer is disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> isn't defined, or the record would take the stream past <see cref="MaxCapacity"/>.
    /// </exception>
    /// <exception cref="OutOfMemoryException">Growing the block failed. The writer is unchanged.</exception>
    internal void Append<T>(CommandKind kind, in T payload)
        where T : unmanaged
        => Append(kind, in payload, ReadOnlySpan<byte>.Empty);

    /// <summary>Appends a record with a trailing part.</summary>
    /// <typeparam name="T">The kind's payload struct.</typeparam>
    /// <typeparam name="TTrailing">The kind's trailing element type.</typeparam>
    /// <param name="kind">The record's kind: a defined <see cref="CommandKind"/>.</param>
    /// <param name="payload">The payload, copied into the stream.</param>
    /// <param name="trailing">The trailing elements, copied into the stream after the payload; may be empty.</param>
    /// <exception cref="ObjectDisposedException">The writer is disposed.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="kind"/> isn't defined, or the record would take the stream past <see cref="MaxCapacity"/>
    /// (including a trailing part of more than <see cref="int.MaxValue"/> bytes).
    /// </exception>
    /// <exception cref="OutOfMemoryException">Growing the block failed. The writer is unchanged.</exception>
    internal void Append<T, TTrailing>(CommandKind kind, in T payload, ReadOnlySpan<TTrailing> trailing)
        where T : unmanaged
        where TTrailing : unmanaged
    {
        ThrowIfDisposed();
        // The kinds are contiguous from 0, so one unsigned comparison rejects every undefined value.
        if ((ushort)kind > (ushort)CommandKind.ClearSwapchainImage)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind isn't a defined CommandKind.");
        }

        // Sizes in long arithmetic: a span's byte count can exceed int, and none of these sums overflow a long.
        long trailingOffset = CommandRecordHeader.TrailingOffset(sizeof(T));
        long trailingBytes = (long)trailing.Length * sizeof(TTrailing);
        long size = trailingOffset + trailingBytes;
        long padded = CommandRecordHeader.AlignUp(size);
        if (padded > MaxCapacity - _length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(trailing), trailingBytes, "The record would make the command stream larger than its maximum size.");
        }

        long required = _length + padded;
        if (required > _capacity)
        {
            Grow(required);
        }

        // Sound: the check and growth above leave _length + padded <= _capacity, so every write below stays inside
        // the block. Records start at multiples of 8 in an 8-byte aligned block, so the header and payload writes are
        // aligned; the trailing copy is a byte copy, which needs no alignment.
        byte* record = _buffer + _length;
        new Span<byte>(record, (int)padded).Clear();
        *(CommandRecordHeader*)record = new CommandRecordHeader(kind, (int)size);
        *(T*)(record + CommandRecordHeader.Alignment) = payload;
        MemoryMarshal.AsBytes(trailing).CopyTo(new Span<byte>(record + trailingOffset, (int)trailingBytes));
        _length = (int)required;
    }

    /// <summary>Empties the stream and keeps the block, so the next frame's records reuse it.</summary>
    /// <exception cref="ObjectDisposedException">The writer is disposed.</exception>
    internal void Reset()
    {
        ThrowIfDisposed();
        _length = 0;
    }

    /// <summary>Frees the block. Later calls do nothing; every other member then throws.</summary>
    public void Dispose()
    {
        if (_buffer == null)
        {
            return;
        }

        _allocator.Free(_buffer);
        _buffer = null;
        _capacity = 0;
        _length = 0;
    }

    // Doubles the capacity until the stream holds required bytes, capped at MaxCapacity (the caller checked that
    // required fits). Allocates the new block before touching any state, so a failure leaves the writer unchanged.
    private void Grow(long required)
    {
        long capacity = _capacity;
        while (capacity < required)
        {
            capacity = Math.Min(capacity * 2, MaxCapacity);
        }

        byte* buffer = (byte*)_allocator.Allocate((nuint)capacity, CommandRecordHeader.Alignment);

        // Sound: both blocks hold at least _length bytes (capacity > _capacity >= _length), and they are separate
        // allocations, so they don't overlap. The old block is freed only after the copy.
        new ReadOnlySpan<byte>(_buffer, _length).CopyTo(new Span<byte>(buffer, _length));
        _allocator.Free(_buffer);
        _buffer = buffer;
        _capacity = (int)capacity;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_buffer == null, this);
}
