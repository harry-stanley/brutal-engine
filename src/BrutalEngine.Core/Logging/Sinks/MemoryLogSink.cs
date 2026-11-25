using System;
using System.Diagnostics;
using System.Threading;

namespace BrutalEngine.Core.Logging.Sinks;

/// <summary>
/// A sink that keeps the most recent log entries in fixed, preallocated memory: the log tail for crash reports
/// and the debug overlay.
/// </summary>
/// <remarks>
/// <para>
/// The sink holds at most <see cref="CapacityEntries"/> entries whose messages total at most
/// <see cref="CapacityBytes"/> bytes. Its storage is allocated once, by the constructor, and never grows. When
/// a new entry doesn't fit either limit, the oldest entries are overwritten and counted in
/// <see cref="DroppedCount"/>. A message longer than <see cref="CapacityBytes"/> is stored cut on a code-point
/// boundary, ending with <c>…</c> (U+2026), within <see cref="CapacityBytes"/>.
/// </para>
/// <para>
/// Read the entries with <see cref="CopyTo"/>, which copies them out under the sink's lock, so a copy never
/// contains a half-written entry.
/// </para>
/// <para>
/// Thread-safe: every member may be called from any thread, and the sink may be shared by several loggers.
/// <see cref="Write"/>, <see cref="CopyTo"/> and <see cref="DroppedCount"/> don't allocate. The sink holds no
/// unmanaged resources, so it isn't disposable.
/// </para>
/// </remarks>
public sealed class MemoryLogSink : ILogSink
{
    /// <summary>The smallest <see cref="CapacityBytes"/>: the logger's smallest maximum message size.</summary>
    internal const int SmallestCapacityBytes = 64;

    private readonly Lock _lock = new();

    // Entry metadata, oldest at _first, as a ring of _count slots.
    private readonly Slot[] _slots;

    // Message bytes, oldest message starting at _byteStart, as a ring of _byteCount bytes. A message may wrap.
    private readonly byte[] _bytes;
    private int _first;
    private int _count;
    private int _byteStart;
    private int _byteCount;
    private long _droppedCount;

    /// <summary>Creates a memory sink and allocates its storage.</summary>
    /// <param name="capacityEntries">The most entries kept. At least 1.</param>
    /// <param name="capacityBytes">The most message bytes kept, over all entries. At least 64.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capacityEntries"/> is less than 1, or <paramref name="capacityBytes"/> is less than 64.
    /// </exception>
    public MemoryLogSink(int capacityEntries, int capacityBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacityBytes, SmallestCapacityBytes);
        CapacityEntries = capacityEntries;
        CapacityBytes = capacityBytes;
        _slots = new Slot[capacityEntries];
        _bytes = new byte[capacityBytes];
    }

    /// <summary>The most entries the sink keeps.</summary>
    /// <remarks>Fixed at construction. Thread-safe; doesn't allocate.</remarks>
    public int CapacityEntries { get; }

    /// <summary>The most message bytes the sink keeps, over all its entries; also the longest message kept whole.</summary>
    /// <remarks>Fixed at construction. Thread-safe; doesn't allocate.</remarks>
    public int CapacityBytes { get; }

    /// <summary>How many entries have been overwritten to make room for newer ones.</summary>
    /// <remarks>Thread-safe; doesn't allocate.</remarks>
    public long DroppedCount
    {
        get
        {
            lock (_lock)
            {
                return _droppedCount;
            }
        }
    }

    private static ReadOnlySpan<byte> Ellipsis => "\u2026"u8;

    /// <summary>Copies the entry in, overwriting the oldest entries if it doesn't fit.</summary>
    /// <param name="entry">
    /// The entry. Its fields and message bytes are copied. No reference to the entry is kept except its
    /// <see cref="LogEntry.Category"/> and <see cref="LogEntry.CallerFilePath"/> strings.
    /// </param>
    /// <remarks>Thread-safe; doesn't allocate.</remarks>
    /// <exception cref="ArgumentOutOfRangeException">The entry's level is outside Trace to Critical.</exception>
    public void Write(scoped in LogEntry entry)
    {
        LogLineFormatter.ThrowIfInvalidLevel(entry.Level, nameof(entry));
        ReadOnlySpan<byte> message = entry.Message;
        ReadOnlySpan<byte> suffix = default;
        if (message.Length > CapacityBytes)
        {
            // Cut on a code-point boundary: message[cut] is the first byte left out, so it must start a code point.
            // The message is valid UTF-8 (the LogEntry contract), so the kept part stays valid.
            int cut = CapacityBytes - Ellipsis.Length;
            while (cut > 0 && (message[cut] & 0xC0) == 0x80)
            {
                cut--;
            }

            message = message[..cut];
            suffix = Ellipsis;
        }

        int length = message.Length + suffix.Length;
        lock (_lock)
        {
            // An empty sink always has room (length <= CapacityBytes), so the loop stops by _count == 0 at the latest.
            while (_count > 0 && (_count == _slots.Length || _byteCount + length > _bytes.Length))
            {
                DropOldest();
            }

            Debug.Assert(_count > 0 || _byteCount == 0, "An empty ring must hold no message bytes.");

            int start = Wrap(_byteStart + _byteCount, _bytes.Length);
            CopyIn(message, start);
            CopyIn(suffix, Wrap(start + message.Length, _bytes.Length));
            _byteCount += length;

            _slots[Wrap(_first + _count, _slots.Length)] = new Slot
            {
                Level = entry.Level,
                Category = entry.Category,
                Timestamp = entry.Timestamp,
                ThreadId = entry.ThreadId,
                CallerFilePath = entry.CallerFilePath,
                CallerLineNumber = entry.CallerLineNumber,
                MessageStart = start,
                MessageLength = length,
            };
            _count++;
        }
    }

    /// <summary>Does nothing: the entries are already in memory.</summary>
    public void Flush()
    {
    }

    /// <summary>
    /// Copies the newest entries that fit into both buffers, oldest first, and returns how many were copied.
    /// </summary>
    /// <param name="destination">Receives the entries, oldest first, from index 0.</param>
    /// <param name="messageBuffer">
    /// Receives the entries' messages, one after another. Each record's <see cref="MemoryLogRecord.Message"/> is a
    /// slice of it, so it must not be reused while the records are in use.
    /// </param>
    /// <returns>
    /// The number of records written to <paramref name="destination"/>. Every entry the sink holds is copied when
    /// <paramref name="destination"/> has at least <see cref="CapacityEntries"/> elements and
    /// <paramref name="messageBuffer"/> at least <see cref="CapacityBytes"/> bytes. Otherwise the copy stops at the
    /// first entry, going back from the newest, that doesn't fit, so the result is always the most recent entries
    /// without gaps.
    /// </returns>
    /// <remarks>Thread-safe: the copy is taken under the sink's lock, so it is consistent. Doesn't allocate.</remarks>
    public int CopyTo(Span<MemoryLogRecord> destination, Memory<byte> messageBuffer)
    {
        Span<byte> buffer = messageBuffer.Span;
        lock (_lock)
        {
            // Walk back from the newest entry while both buffers have room.
            int taken = 0;
            int bytes = 0;
            int most = Math.Min(destination.Length, _count);
            while (taken < most)
            {
                int length = _slots[Wrap(_first + _count - 1 - taken, _slots.Length)].MessageLength;
                if (bytes + length > buffer.Length)
                {
                    break;
                }

                bytes += length;
                taken++;
            }

            // Then copy them out oldest first.
            int offset = 0;
            for (int i = 0; i < taken; i++)
            {
                ref readonly Slot slot = ref _slots[Wrap(_first + _count - taken + i, _slots.Length)];
                CopyOut(slot.MessageStart, buffer.Slice(offset, slot.MessageLength));
                destination[i] = new MemoryLogRecord(
                    slot.Level,
                    slot.Category,
                    slot.Timestamp,
                    slot.ThreadId,
                    messageBuffer.Slice(offset, slot.MessageLength),
                    slot.CallerFilePath,
                    slot.CallerLineNumber);
                offset += slot.MessageLength;
            }

            return taken;
        }
    }

    // index is below 2 * length in every caller, so one subtraction wraps it.
    private static int Wrap(int index, int length) => index >= length ? index - length : index;

    // Called with _lock held.
    private void DropOldest()
    {
        ref Slot oldest = ref _slots[_first];
        _byteStart = Wrap(_byteStart + oldest.MessageLength, _bytes.Length);
        _byteCount -= oldest.MessageLength;
        oldest = default; // Releases the strings.
        _first = Wrap(_first + 1, _slots.Length);
        _count--;
        _droppedCount++;
    }

    // Called with _lock held. Copies into the byte ring at start, wrapping at its end.
    private void CopyIn(ReadOnlySpan<byte> source, int start)
    {
        int first = Math.Min(source.Length, _bytes.Length - start);
        source[..first].CopyTo(_bytes.AsSpan(start));
        source[first..].CopyTo(_bytes);
    }

    // Called with _lock held. Copies destination.Length bytes out of the byte ring from start, wrapping at its end.
    private void CopyOut(int start, Span<byte> destination)
    {
        int first = Math.Min(destination.Length, _bytes.Length - start);
        _bytes.AsSpan(start, first).CopyTo(destination);
        _bytes.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
    }

    private struct Slot
    {
        public LogLevel Level;
        public string Category;
        public DateTimeOffset Timestamp;
        public int ThreadId;
        public string CallerFilePath;
        public int CallerLineNumber;
        public int MessageStart;
        public int MessageLength;
    }
}
