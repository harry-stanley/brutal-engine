using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>The arguments of a non-indexed draw (docs/design/graphics.md section 9).</summary>
/// <param name="VertexCount">The number of vertices to draw.</param>
/// <param name="InstanceCount">The number of instances to draw. Defaults to 1.</param>
/// <param name="FirstVertex">The index of the first vertex. Defaults to 0.</param>
/// <param name="FirstInstance">The index of the first instance. Defaults to 0.</param>
/// <remarks>
/// <para>
/// A struct rather than four parameters, so the draw follows the options rule of docs/07, and because its layout is the
/// GPU's indirect-draw record: four 32-bit integers in this order, 16 bytes, as Vulkan's <c>VkDrawIndirectCommand</c>.
/// Indirect draws will reuse it. The type itself accepts any values; the encoder that records it decides which it accepts.
/// </para>
/// <para>
/// A plain unmanaged value, so the command stream holds it as raw bytes. Equality compares every field. Immutable and
/// safe to use from any thread. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct DrawArguments(int VertexCount, int InstanceCount = 1, int FirstVertex = 0, int FirstInstance = 0)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>DrawArguments { VertexCount = 3, InstanceCount = 1, FirstVertex = 0, FirstInstance = 0 }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is, so (invalid) negative
    /// values always use an ASCII minus sign. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"DrawArguments {{ VertexCount = {VertexCount}, InstanceCount = {InstanceCount}, FirstVertex = {FirstVertex}, FirstInstance = {FirstInstance} }}");
}
