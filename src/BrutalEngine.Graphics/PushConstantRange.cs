using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>The push-constant bytes a pipeline layout declares, and the shader stages that read them.</summary>
/// <param name="Stages">The stages that read the push constants.</param>
/// <param name="Size">
/// The number of bytes, from offset 0: a multiple of 4, at most <see cref="GraphicsLimits.MaxPushConstantBytes"/>; 0
/// (the default) declares no push constants.
/// </param>
/// <remarks>
/// A layout declares one range, shared by every stage in <see cref="Stages"/> (docs/design/graphics.md section 7). The
/// device checks it when the layout is created: a negative size, a size that isn't a multiple of 4 or flags outside
/// <see cref="ShaderStages"/> throw <see cref="System.ArgumentException"/> (<see cref="System.ArgumentOutOfRangeException"/>
/// for a value out of its range).
/// A plain unmanaged value. Equality compares every field. Immutable and safe to use from any thread. No member
/// allocates, except <see cref="ToString"/>.
/// </remarks>
public readonly record struct PushConstantRange(ShaderStages Stages, int Size)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example <c>PushConstantRange { Stages = Vertex, Fragment, Size = 64 }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is, so negative values
    /// always use an ASCII minus sign. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture, $"PushConstantRange {{ Stages = {Stages}, Size = {Size} }}");
}
