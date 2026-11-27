using System;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Internal;

/// <summary>
/// One persistently mapped <see cref="BackendMemory.HostUpload"/> staging buffer that writes are bump-allocated from: a
/// frame slot's upload ring slice, a pending upload block, or an overflow block (docs/design/graphics.md section 6).
/// </summary>
/// <remarks>
/// The device owns the backend buffer and destroys it. Offsets are multiples of <see cref="Alignment"/>, which every texel
/// size and 4 divide, so a texture copy may start at any of them. Not thread-safe: the device's owner thread only. No
/// member allocates.
/// </remarks>
internal sealed unsafe class UploadRing
{
    /// <summary>The alignment of every staged write, in bytes.</summary>
    internal const int Alignment = 16;

    private readonly byte* _data;
    private long _used;

    /// <summary>Wraps a staging buffer the backend just created.</summary>
    /// <param name="buffer">The buffer's id and mapped pointer.</param>
    /// <param name="capacity">The buffer's size in bytes.</param>
    internal UploadRing(BackendBuffer buffer, long capacity)
    {
        Id = buffer.Id;
        _data = (byte*)buffer.MappedPointer;
        Capacity = capacity;
    }

    /// <summary>The backend buffer's id.</summary>
    internal ulong Id { get; }

    /// <summary>The buffer's size in bytes.</summary>
    internal long Capacity { get; }

    /// <summary>Reserves <paramref name="size"/> bytes at the next aligned offset.</summary>
    /// <param name="size">The bytes needed; positive.</param>
    /// <returns>The offset of the reserved bytes, or -1 when they don't fit (nothing is reserved).</returns>
    internal long TryReserve(long size)
    {
        long offset = (_used + Alignment - 1) & ~(long)(Alignment - 1);
        if (size > Capacity - offset)
        {
            return -1;
        }

        _used = offset + size;
        return offset;
    }

    /// <summary>The mapped bytes at an offset, for the caller to fill.</summary>
    /// <param name="offset">An offset inside a reservation.</param>
    /// <param name="length">The bytes wanted; the range stays inside that reservation.</param>
    /// <returns>The bytes, valid until the buffer is destroyed.</returns>
    // Sound: callers stay inside a range TryReserve returned, which lies inside the mapped buffer of Capacity bytes.
    internal Span<byte> Bytes(long offset, int length) => new(_data + offset, length);

    /// <summary>Frees every reservation, once the GPU has finished reading them.</summary>
    internal void Reset() => _used = 0;
}
