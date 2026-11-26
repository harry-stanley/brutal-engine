using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>Describes a sampler to create: filtering, addressing, anisotropy and an optional depth comparison.</summary>
/// <remarks>
/// <para>
/// The device validates a description before the backend sees it (docs/design/graphics.md section 5). A value outside
/// its enum, or a <see cref="MaxAnisotropy"/> that is zero, negative or NaN, throws
/// <see cref="System.ArgumentOutOfRangeException"/>. Above 1, anisotropy needs
/// <see cref="GraphicsFeatures.SamplerAnisotropy"/> (<see cref="System.InvalidOperationException"/> without it) and
/// must not exceed <see cref="GraphicsLimits.MaxSamplerAnisotropy"/> (<see cref="System.ArgumentOutOfRangeException"/>).
/// </para>
/// <para>
/// Created with <c>new SamplerDescription()</c>, a sampler filters with <see cref="Filter.Nearest"/> (the common
/// pixel-art case), clamps to the edge, has no anisotropy (<see cref="MaxAnisotropy"/> 1) and no comparison;
/// <c>default(SamplerDescription)</c> has a <see cref="MaxAnisotropy"/> of 0, and is invalid. An immutable value: safe
/// to copy and to use from any thread. Equality compares every property; <see cref="MaxAnisotropy"/> compares exactly
/// and the debug name ordinally. The device keeps no reference to the description or its <see cref="DebugName"/>. No
/// member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct SamplerDescription
{
    /// <summary>Creates a sampler description with the defaults: nearest filtering, clamp to edge, no anisotropy, no comparison.</summary>
    public SamplerDescription()
    {
    }

    /// <summary>The filter when the texture is minified. Defaults to <see cref="Filter.Nearest"/>.</summary>
    public Filter MinFilter { get; init; }

    /// <summary>The filter when the texture is magnified. Defaults to <see cref="Filter.Nearest"/>.</summary>
    public Filter MagFilter { get; init; }

    /// <summary>The filter between mip levels. Defaults to <see cref="Filter.Nearest"/>.</summary>
    public Filter MipmapFilter { get; init; }

    /// <summary>How the horizontal coordinate is addressed outside [0, 1]. Defaults to <see cref="AddressMode.ClampToEdge"/>.</summary>
    public AddressMode AddressU { get; init; }

    /// <summary>How the vertical coordinate is addressed outside [0, 1]. Defaults to <see cref="AddressMode.ClampToEdge"/>.</summary>
    public AddressMode AddressV { get; init; }

    /// <summary>
    /// The color outside the texture with <see cref="AddressMode.ClampToBorder"/>; ignored with the other modes.
    /// Defaults to <see cref="BorderColor.TransparentBlack"/>.
    /// </summary>
    public BorderColor BorderColor { get; init; }

    /// <summary>
    /// The largest anisotropy: greater than 0. Defaults to 1, no anisotropic filtering. Above 1 it
    /// needs <see cref="GraphicsFeatures.SamplerAnisotropy"/> and must not exceed
    /// <see cref="GraphicsLimits.MaxSamplerAnisotropy"/>.
    /// </summary>
    public float MaxAnisotropy { get; init; } = 1;

    /// <summary>The comparison of a depth-compare sampler; <see langword="null"/> (the default) for an ordinary sampler.</summary>
    public CompareFunction? Compare { get; init; }

    /// <summary>
    /// An optional name for debuggers, validation messages, errors and the leak report (docs/design/graphics.md
    /// section 13); <see langword="null"/> for none.
    /// </summary>
    public string? DebugName { get; init; }

    /// <summary>
    /// Returns every property, for example
    /// <c>SamplerDescription { MinFilter = Linear, MagFilter = Linear, MipmapFilter = Nearest, AddressU = Repeat, AddressV = Repeat, BorderColor = TransparentBlack, MaxAnisotropy = 4, Compare = , DebugName = tiles }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"SamplerDescription {{ MinFilter = {MinFilter}, MagFilter = {MagFilter}, MipmapFilter = {MipmapFilter}, AddressU = {AddressU}, AddressV = {AddressV}, BorderColor = {BorderColor}, MaxAnisotropy = {MaxAnisotropy}, Compare = {Compare}, DebugName = {DebugName} }}");
}
