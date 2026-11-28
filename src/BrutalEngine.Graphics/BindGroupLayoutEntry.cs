using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>One binding a bind group layout declares: its number, the stages that see it, its type and array size.</summary>
/// <param name="Binding">The binding number shaders declare: 0 or more, unique in the layout.</param>
/// <param name="Visibility">The shader stages that access the binding.</param>
/// <param name="Type">The kind of resource bound.</param>
/// <param name="Count">The array size: at least 1, the default; above 1 makes the binding an array of resources.</param>
/// <remarks>
/// The device checks a layout when it is created (docs/design/graphics.md section 8): a duplicate binding number or a
/// <paramref name="Count"/> below 1 throws <see cref="System.ArgumentException"/>. A plain unmanaged value. Equality
/// compares every field. Immutable and safe to use from any thread. No member allocates, except <see cref="ToString"/>.
/// </remarks>
public readonly record struct BindGroupLayoutEntry(int Binding, ShaderStages Visibility, BindingType Type, int Count = 1)
{
    /// <summary>
    /// Returns every field in the record's usual form, for example
    /// <c>BindGroupLayoutEntry { Binding = 0, Visibility = Vertex, Fragment, Type = UniformBuffer, Count = 1 }</c>.
    /// </summary>
    /// <returns>
    /// A diagnostic string, formatted with the invariant culture whatever the current culture is, so negative values
    /// always use an ASCII minus sign. Allocates.
    /// </returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"BindGroupLayoutEntry {{ Binding = {Binding}, Visibility = {Visibility}, Type = {Type}, Count = {Count} }}");
}
