using System;
using System.Buffers;
using System.Threading;

namespace BrutalEngine.Core.Logging;

/// <summary>
/// The message buffers of one <see cref="Logger"/>: a few buffers of exactly the logger's maximum message size,
/// kept by the logger itself so that a message buffer doesn't depend on <see cref="ArrayPool{T}.Shared"/> keeping an
/// array. The handler's other temporary buffers (a value too long for the message, a copy of invalid UTF-8) are rare
/// and still come from the shared pool.
/// </summary>
/// <remarks>
/// <para>
/// The shared pool trims itself after a gen2 collection. Under high memory pressure the trim drops every thread's
/// cached arrays, so the next rent of a message buffer from it allocates a new one. These buffers stay with the
/// logger instead: each slot creates its buffer the first time it is rented, and the buffer lives as long as the
/// cache, without ever being given to another pool.
/// </para>
/// <para>
/// Each slot owns one buffer for good and has a claim flag. <see cref="Rent"/> claims the first free slot with a
/// compare-and-swap; <see cref="Return"/> finds the slot by the buffer's identity and releases the claim. A buffer
/// is therefore held by one renter at a time, and the origin of a returned buffer is known from its identity, not its
/// size. When every slot is claimed (more threads inside a logging call at once than there are slots),
/// <see cref="Rent"/> falls back to the fallback pool, and <see cref="Return"/> gives such a buffer back to it.
/// </para>
/// <para>
/// Thread-safe and lock-free: any number of threads may rent and return at once, and a buffer may be returned from
/// another thread than the one that rented it. Once a slot's buffer exists, renting and returning it allocate
/// nothing. Owned by the logger as instance state; there is nothing to release (the buffers are managed arrays and
/// are collected with the cache).
/// </para>
/// </remarks>
internal sealed class MessageBufferCache
{
    /// <summary>
    /// Slots in a logger's cache. Eight covers the threads that realistically log at the same moment (a main, a
    /// render and a few worker threads), while a logger that only ever logs from one thread creates one buffer:
    /// slots are filled lazily. Dispatch to the sinks is serialised anyway, so more slots would only let more threads
    /// format while they wait for the lock. At the largest message size the cache holds at most 512 KiB.
    /// </summary>
    internal const int DefaultSlotCount = 8;

    private readonly Slot[] _slots;
    private readonly int _bufferBytes;
    private readonly ArrayPool<byte> _fallback;

    /// <summary>Creates an empty cache. No buffer is created until it is first rented.</summary>
    /// <param name="bufferBytes">Length of every cached buffer, and the minimum length rented from the fallback.</param>
    /// <param name="slotCount">Number of slots, which bounds how many buffers the cache creates.</param>
    /// <param name="fallback">Pool used when every slot is claimed. A logger passes <see cref="ArrayPool{T}.Shared"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bufferBytes"/> or <paramref name="slotCount"/> isn't positive.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="fallback"/> is <see langword="null"/>.</exception>
    internal MessageBufferCache(int bufferBytes, int slotCount, ArrayPool<byte> fallback)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotCount);
        ArgumentNullException.ThrowIfNull(fallback);
        _slots = new Slot[slotCount];
        _bufferBytes = bufferBytes;
        _fallback = fallback;
    }

    /// <summary>Rents a buffer: a cached one if a slot is free, otherwise one from the fallback pool.</summary>
    /// <returns>
    /// A buffer of at least the cache's buffer length, held by the caller alone until it passes it to
    /// <see cref="Return"/>. A cached buffer is exactly that long; one from the fallback may be longer.
    /// </returns>
    /// <remarks>
    /// Lock-free. Allocates only when a slot is rented for the first time, or when the fallback pool allocates.
    /// A buffer that is never returned keeps its slot claimed, so callers return it on every path.
    /// </remarks>
    internal byte[] Rent()
    {
        Slot[] slots = _slots;
        for (int i = 0; i < slots.Length; i++)
        {
            ref Slot slot = ref slots[i];
            if (Interlocked.CompareExchange(ref slot.Claimed, 1, 0) == 0)
            {
                // The claim makes this caller the only one using the slot until it returns the buffer, so creating
                // the buffer here can't race. Volatile.Write publishes it to a Return on another thread.
                byte[]? buffer = slot.Buffer;
                if (buffer is null)
                {
                    buffer = new byte[_bufferBytes];
                    Volatile.Write(ref slot.Buffer, buffer);
                }

                return buffer;
            }
        }

        return _fallback.Rent(_bufferBytes);
    }

    /// <summary>
    /// Returns a buffer from <see cref="Rent"/>: a cached buffer frees its slot, any other buffer goes back to the
    /// fallback pool. The caller must not use the buffer afterwards.
    /// </summary>
    /// <param name="buffer">The buffer. Its contents aren't cleared.</param>
    /// <remarks>Lock-free, and allocates nothing.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">
    /// <paramref name="buffer"/> is a cached buffer whose slot isn't claimed: it was returned twice. A second return
    /// that comes after the slot has been rented again can't be detected.
    /// </exception>
    internal void Return(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Slot[] slots = _slots;
        for (int i = 0; i < slots.Length; i++)
        {
            ref Slot slot = ref slots[i];
            if (ReferenceEquals(Volatile.Read(ref slot.Buffer), buffer))
            {
                // Releases the claim after every write the renter made to the buffer.
                if (Interlocked.Exchange(ref slot.Claimed, 0) == 0)
                {
                    throw new InvalidOperationException("The message buffer was returned twice.");
                }

                return;
            }
        }

        _fallback.Return(buffer);
    }

    /// <summary>One slot: its buffer once created, and whether a renter holds it (1) or not (0).</summary>
    private struct Slot
    {
        public byte[]? Buffer;
        public int Claimed;
    }
}
