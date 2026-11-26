using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>The arguments of an indexed draw (docs/design/graphics.md section 9).</summary>
/// <param name="IndexCount">The number of indices to draw.</param>
/// <param name="InstanceCount">The number of instances to draw. Defaults to 1.</param>
/// <param name="FirstIndex">The position of the first index in the index buffer. Defaults to 0.</param>
/// <param name="BaseVertex">The value added to each index before it selects a vertex; may be negative. Defaults to 0.</param>
/// <param name="FirstInstance">The index of the first instance. Defaults to 0.</param>
/// <remarks>
/// <para>
/// A struct rather than five parameters, so the draw follows the options rule of docs/07, and because its layout is the
/// GPU's indexed indirect-draw record: five 32-bit integers in this order, 20 bytes, as Vulkan's
/// <c>VkDrawIndexedIndirectCommand</c>. Indirect draws will reuse it. The type itself accepts any values; the encoder
/// that records it decides which it accepts.
/// </para>
/// <para>
/// A plain unmanaged value, so the command stream holds it as raw bytes. Equality compares every field. Immutable and
/// safe to use from any thread. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct DrawIndexedArguments(
    int IndexCount, int InstanceCount = 1, int FirstIndex = 0, int BaseVertex = 0, int FirstInstance = 0)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>DrawIndexedArguments { IndexCount = 6, InstanceCount = 1, FirstIndex = 0, BaseVertex = 0, FirstInstance = 0 }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is, so negative values
    /// always use an ASCII minus sign. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"DrawIndexedArguments {{ IndexCount = {IndexCount}, InstanceCount = {InstanceCount}, FirstIndex = {FirstIndex}, BaseVertex = {BaseVertex}, FirstInstance = {FirstInstance} }}");
}
