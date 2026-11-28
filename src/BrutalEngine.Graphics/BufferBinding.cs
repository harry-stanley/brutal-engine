using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>A byte range of a buffer that a bind group entry binds.</summary>
/// <param name="Buffer">The buffer bound.</param>
/// <param name="Offset">The first byte of the range; 0 by default.</param>
/// <param name="Size">The bytes bound; 0 (the default) binds from <paramref name="Offset"/> to the end of the buffer.</param>
/// <remarks>
/// Made into an entry by <see cref="BindGroupEntry.FromBuffer"/>, and read back from <see cref="BindGroupEntry.Buffer"/>,
/// which also gives a transient allocation's ring alias and offset (docs/design/graphics.md section 8). The range is
/// recorded as given: the device checks it against the buffer, its usage and the offset alignments when the group is
/// created. A plain unmanaged value. Equality compares every field. Immutable and safe to use from any thread. No member
/// allocates, except <see cref="ToString"/>.
/// </remarks>
public readonly record struct BufferBinding(BufferHandle Buffer, long Offset = 0, long Size = 0)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example <c>BufferBinding { Buffer = Buffer(1:1), Offset = 256, Size = 64 }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is, so negative values
    /// always use an ASCII minus sign. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"BufferBinding {{ Buffer = {Buffer}, Offset = {Offset}, Size = {Size} }}");
}
