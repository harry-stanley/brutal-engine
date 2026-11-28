using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>One attribute of a vertex buffer: where it lies in each element and which shader input reads it.</summary>
/// <param name="Location">The shader input location that reads the attribute: at least 0, unique across the pipeline's vertex buffers.</param>
/// <param name="Format">The attribute's type and component count, which fix its size in bytes.</param>
/// <param name="Offset">
/// The byte offset of the attribute in each element: at least 0, with the attribute's last byte inside the buffer's
/// <see cref="VertexBufferLayout.Stride"/>.
/// </param>
/// <remarks>
/// The device checks the rules above when the pipeline is created (docs/design/graphics.md section 7) and throws
/// <see cref="System.ArgumentException"/>, or <see cref="System.ArgumentOutOfRangeException"/> for a value out of its range.
/// A plain unmanaged value. Equality compares every field. Immutable and safe to use from any thread. No member
/// allocates, except <see cref="ToString"/>.
/// </remarks>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "The design note's name (docs/design/graphics.md section 7), as WebGPU's and Vulkan's: a vertex attribute, not a .NET attribute.")]
public readonly record struct VertexAttribute(int Location, VertexFormat Format, int Offset)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example <c>VertexAttribute { Location = 1, Format = Float32x2, Offset = 8 }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is, so negative values
    /// always use an ASCII minus sign. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"VertexAttribute {{ Location = {Location}, Format = {Format}, Offset = {Offset} }}");
}
