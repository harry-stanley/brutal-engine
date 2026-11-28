using System;
using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>The layout of one vertex buffer a graphics pipeline reads: its element size, step rate and attributes.</summary>
/// <param name="Stride">
/// The bytes from one element to the next: at least 0, with no alignment rule, and every attribute lies inside it.
/// </param>
/// <param name="StepMode">Whether the buffer advances per vertex or per instance.</param>
/// <param name="Attributes">The attributes each element holds; may be empty.</param>
/// <remarks>
/// The index of the layout in <see cref="GraphicsPipelineDescription.VertexBuffers"/> is the vertex buffer slot a draw
/// binds the buffer to (docs/design/graphics.md section 7). The device checks the layout when the pipeline is created.
/// An immutable value: safe to copy and to use from any thread. Equality compares <see cref="Attributes"/> by reference,
/// as <see cref="ReadOnlyMemory{T}"/> does. No member allocates, except <see cref="ToString"/>.
/// </remarks>
public readonly record struct VertexBufferLayout(int Stride, VertexStepMode StepMode, ReadOnlyMemory<VertexAttribute> Attributes)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>VertexBufferLayout { Stride = 20, StepMode = Vertex, Attributes = System.ReadOnlyMemory&lt;VertexAttribute&gt;[2] }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string whose numeric fields are formatted with the invariant culture whatever the current culture is;
    /// an undefined enum value prints its number as <see cref="System.Enum.ToString()"/> does. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"VertexBufferLayout {{ Stride = {Stride}, StepMode = {StepMode}, Attributes = {Attributes} }}");
}
