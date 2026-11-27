using System;
using System.Diagnostics;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Internal;

/// <summary>
/// One persistently mapped <see cref="BackendMemory.HostUpload"/> staging buffer that writes are bump-allocated from: a
/// frame slot's upload ring slice, a pending upload block, or an overflow block (docs/design/graphics.md section 6).
/// </summary>
/// <remarks>
/// The device owns the backend buffer and destroys it. Offsets are multiples of <see cref="Alignment"/>, which every texel
/// size and 4 divide, so a texture copy may start at any of them; a transient allocation asks for a larger alignment
/// when its usage needs one. Not thread-safe: the device's owner thread only. No member allocates.
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

    /// <summary>
    /// The open frame's alias handle of the buffer, which its transient allocations hand out; the null handle while it
    /// has none. Set and cleared by the device.
    /// </summary>
    internal Handle Alias { get; set; }

    /// <summary>Reserves <paramref name="size"/> bytes at the next offset that is a multiple of <see cref="Alignment"/>.</summary>
    /// <param name="size">The bytes needed; positive.</param>
    /// <returns>The offset of the reserved bytes, or -1 when they don't fit (nothing is reserved).</returns>
    internal long TryReserve(long size) => TryReserve(size, Alignment);

    /// <summary>Reserves <paramref name="size"/> bytes at the next offset that is a multiple of <paramref name="alignment"/>.</summary>
    /// <param name="size">The bytes needed; positive.</param>
    /// <param name="alignment">
    /// The offset's alignment: a power of two, as Vulkan guarantees for its offset alignment limits, and at least 16.
    /// </param>
    /// <returns>The offset of the reserved bytes, or -1 when they don't fit, padding included (nothing is reserved).</returns>
    internal long TryReserve(long size, long alignment)
    {
        Debug.Assert(alignment >= Alignment && (alignment & (alignment - 1)) == 0, "The alignment must be a power of two of at least 16.");

        // _used <= Capacity and the alignment is small, so the rounding can't overflow; an offset past the end leaves
        // Capacity - offset negative, which no positive size fits.
        long offset = (_used + alignment - 1) & ~(alignment - 1);
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
