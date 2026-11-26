using System.Runtime.InteropServices;
using BrutalEngine.Graphics.Backend;

namespace BrutalEngine.Graphics.Recording;

/// <summary>
/// The 8-byte header that starts every record in a command stream, and the record layout around it
/// (docs/design/graphics.md, "The frame plan"). The command writer writes it and the command reader reads it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> A record is this header, then the payload struct, padded with zero bytes to a multiple of
/// <see cref="Alignment"/>, then the trailing part (if any), then zero bytes up to the next multiple of
/// <see cref="Alignment"/>, where the next record starts. Records start at multiples of <see cref="Alignment"/> from
/// the stream's start, so the payload and the trailing part are 8-byte aligned whenever the stream is: the reader can
/// hand both out as references and spans without unaligned access.
/// </para>
/// <para>
/// <see cref="Size"/> counts the header, the padded payload and the trailing part, without the final padding, so the
/// trailing part's exact byte count is <see cref="Size"/> minus <see cref="TrailingOffset"/> of the payload size.
/// </para>
/// <para>
/// A plain unmanaged value with sequential layout: <see cref="Kind"/> at byte 0, <see cref="Reserved"/> at byte 2,
/// <see cref="Size"/> at byte 4, in the machine's byte order. Immutable; no member allocates.
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct CommandRecordHeader
{
    /// <summary>The alignment, in bytes, of every record, payload and trailing part, and the header's size.</summary>
    internal const int Alignment = 8;

    /// <summary>Creates a header.</summary>
    /// <param name="kind">The record's kind.</param>
    /// <param name="size">The record's size, as <see cref="Size"/> defines it.</param>
    internal CommandRecordHeader(CommandKind kind, int size)
    {
        Kind = kind;
        Reserved = 0;
        Size = size;
    }

    /// <summary>The record's kind, which says which payload struct follows.</summary>
    internal CommandKind Kind { get; }

    /// <summary>Always 0. Keeps <see cref="Size"/> 4-byte aligned, and room for a later format change.</summary>
    internal ushort Reserved { get; }

    /// <summary>
    /// The record's size in bytes from the start of the header to the end of the trailing part: the header, the payload
    /// padded to <see cref="Alignment"/>, and the trailing part. The padding after the trailing part isn't counted.
    /// </summary>
    internal int Size { get; }

    /// <summary>Where a record's trailing part starts, from the start of its header.</summary>
    /// <param name="payloadSize">The payload struct's size in bytes: at least 1.</param>
    /// <returns>The header's size plus <paramref name="payloadSize"/> rounded up to <see cref="Alignment"/>.</returns>
    internal static long TrailingOffset(int payloadSize) => Alignment + AlignUp(payloadSize);

    /// <summary>Rounds a non-negative byte count up to a multiple of <see cref="Alignment"/>.</summary>
    /// <param name="value">The byte count: from 0 to <see cref="long.MaxValue"/> minus 7.</param>
    /// <returns>The smallest multiple of <see cref="Alignment"/> that is at least <paramref name="value"/>.</returns>
    internal static long AlignUp(long value) => (value + (Alignment - 1)) & ~(long)(Alignment - 1);
}
