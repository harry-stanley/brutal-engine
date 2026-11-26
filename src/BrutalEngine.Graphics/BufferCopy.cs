using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>A copy of a byte range from one buffer to another, or to another range of the same buffer.</summary>
/// <remarks>
/// <para>
/// The device checks a copy before it records it (docs/design/graphics.md section 6): the source has
/// <see cref="BufferUsage.CopySource"/> and the destination <see cref="BufferUsage.CopyDestination"/>; both offsets and
/// the size are multiples of 4 and the size is positive; both ranges lie inside their buffers; and a buffer can't copy
/// onto an overlapping range of itself. A violation throws <see cref="System.ArgumentException"/>
/// (<see cref="System.ArgumentOutOfRangeException"/> for ranges).
/// </para>
/// <para>
/// An immutable value: safe to copy and to use from any thread. Equality compares every property. The device keeps no
/// reference to the copy. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct BufferCopy
{
    /// <summary>The buffer copied from.</summary>
    public required BufferHandle Source { get; init; }

    /// <summary>The byte offset of the range in the source: at least 0 and a multiple of 4. Defaults to 0.</summary>
    public long SourceOffset { get; init; }

    /// <summary>The buffer copied to; may be <see cref="Source"/> when the ranges don't overlap.</summary>
    public required BufferHandle Destination { get; init; }

    /// <summary>The byte offset of the range in the destination: at least 0 and a multiple of 4. Defaults to 0.</summary>
    public long DestinationOffset { get; init; }

    /// <summary>The bytes copied: greater than 0 and a multiple of 4.</summary>
    public required long Size { get; init; }

    /// <summary>
    /// Returns every property, for example
    /// <c>BufferCopy { Source = Buffer(1:1), SourceOffset = 0, Destination = Buffer(2:1), DestinationOffset = 256, Size = 1024 }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"BufferCopy {{ Source = {Source}, SourceOffset = {SourceOffset}, Destination = {Destination}, DestinationOffset = {DestinationOffset}, Size = {Size} }}");
}
