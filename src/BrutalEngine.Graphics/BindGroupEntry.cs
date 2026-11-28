using System;
using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>One resource of a bind group: a buffer range, a texture view or a sampler, at a binding and array element.</summary>
/// <remarks>
/// <para>
/// Made only through the factories, one per binding (and array element of an array binding); the read side
/// (<see cref="Kind"/> and the property it names) is for the front end's validation and the backends
/// (docs/design/graphics.md section 8). A factory throws for a negative binding or array element
/// (<see cref="ArgumentOutOfRangeException"/>) or a null handle (<see cref="ArgumentException"/>). Reading the resource
/// property of another kind throws <see cref="InvalidOperationException"/>. <c>default(BindGroupEntry)</c> is a
/// <see cref="BindGroupEntryKind.Buffer"/> entry at binding 0 with the null buffer, which the device rejects.
/// </para>
/// <para>
/// An immutable value: safe to copy and to use from any thread. Equality compares the kind, binding, array element and
/// resource. No member allocates, except <see cref="ToString"/>; a failing factory or read builds its exception.
/// </para>
/// </remarks>
public readonly record struct BindGroupEntry
{
    private readonly BufferBinding _buffer;
    private readonly TextureViewHandle _view;
    private readonly SamplerHandle _sampler;

    private BindGroupEntry(BindGroupEntryKind kind, int binding, int arrayElement, BufferBinding buffer, TextureViewHandle view, SamplerHandle sampler)
    {
        Kind = kind;
        Binding = binding;
        ArrayElement = arrayElement;
        _buffer = buffer;
        _view = view;
        _sampler = sampler;
    }

    /// <summary>The kind of resource the entry holds, which names the one resource property it can read.</summary>
    public BindGroupEntryKind Kind { get; }

    /// <summary>The binding number in the layout: 0 or more.</summary>
    public int Binding { get; }

    /// <summary>The element of an array binding: 0 or more; always 0 for a buffer entry.</summary>
    public int ArrayElement { get; }

    /// <summary>
    /// The buffer range bound. For an entry made by <see cref="FromTransient"/>, the allocation's ring alias, its offset
    /// and the size given.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> isn't <see cref="BindGroupEntryKind.Buffer"/>.</exception>
    public BufferBinding Buffer => Kind == BindGroupEntryKind.Buffer ? _buffer : throw WrongKind(BindGroupEntryKind.Buffer);

    /// <summary>The texture view bound.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> isn't <see cref="BindGroupEntryKind.Texture"/>.</exception>
    public TextureViewHandle View => Kind == BindGroupEntryKind.Texture ? _view : throw WrongKind(BindGroupEntryKind.Texture);

    /// <summary>The sampler bound.</summary>
    /// <exception cref="InvalidOperationException"><see cref="Kind"/> isn't <see cref="BindGroupEntryKind.Sampler"/>.</exception>
    public SamplerHandle Sampler => Kind == BindGroupEntryKind.Sampler ? _sampler : throw WrongKind(BindGroupEntryKind.Sampler);

    /// <summary>Makes an entry that binds a range of a buffer.</summary>
    /// <param name="binding">The binding number: 0 or more.</param>
    /// <param name="buffer">The range; its buffer must not be the null handle. The range is checked when the group is created.</param>
    /// <returns>A <see cref="BindGroupEntryKind.Buffer"/> entry at array element 0.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="binding"/> is negative.</exception>
    /// <exception cref="ArgumentException">The range's buffer is the null handle.</exception>
    public static BindGroupEntry FromBuffer(int binding, in BufferBinding buffer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(binding);
        if (buffer.Buffer.IsNull)
        {
            throw new ArgumentException("The buffer binding's buffer is the null handle.", nameof(buffer));
        }

        return new(BindGroupEntryKind.Buffer, binding, 0, buffer, default, default);
    }

    /// <summary>
    /// Makes an entry that binds the first <paramref name="size"/> bytes of a transient allocation, through its ring
    /// alias. Only a <see cref="BindGroupLifetime.Frame"/> group of the allocation's frame may use it.
    /// </summary>
    /// <param name="binding">The binding number: 0 or more.</param>
    /// <param name="allocation">The allocation; not <c>default</c>.</param>
    /// <param name="size">The bytes bound: from 1 to the allocation's length.</param>
    /// <returns>
    /// A <see cref="BindGroupEntryKind.Buffer"/> entry at array element 0 whose <see cref="Buffer"/> is the allocation's
    /// buffer, its offset and <paramref name="size"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="binding"/> is negative, or <paramref name="size"/> is outside 1 to the allocation's length.</exception>
    /// <exception cref="ArgumentException">The allocation's buffer is the null handle.</exception>
    public static BindGroupEntry FromTransient(int binding, in TransientAllocation allocation, long size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(binding);
        if (allocation.Buffer.IsNull)
        {
            throw new ArgumentException("The transient allocation's buffer is the null handle.", nameof(allocation));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, allocation.Data.Length);
        return new(BindGroupEntryKind.Buffer, binding, 0, new(allocation.Buffer, allocation.Offset, size), default, default);
    }

    /// <summary>Makes an entry that binds a texture view.</summary>
    /// <param name="binding">The binding number: 0 or more.</param>
    /// <param name="view">The view; not the null handle.</param>
    /// <param name="arrayElement">The element of an array binding: 0 or more; 0 by default.</param>
    /// <returns>A <see cref="BindGroupEntryKind.Texture"/> entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="binding"/> or <paramref name="arrayElement"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="view"/> is the null handle.</exception>
    public static BindGroupEntry FromTexture(int binding, TextureViewHandle view, int arrayElement = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(binding);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayElement);
        if (view.IsNull)
        {
            throw new ArgumentException("The texture view is the null handle.", nameof(view));
        }

        return new(BindGroupEntryKind.Texture, binding, arrayElement, default, view, default);
    }

    /// <summary>Makes an entry that binds a sampler.</summary>
    /// <param name="binding">The binding number: 0 or more.</param>
    /// <param name="sampler">The sampler; not the null handle.</param>
    /// <param name="arrayElement">The element of an array binding: 0 or more; 0 by default.</param>
    /// <returns>A <see cref="BindGroupEntryKind.Sampler"/> entry.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="binding"/> or <paramref name="arrayElement"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="sampler"/> is the null handle.</exception>
    public static BindGroupEntry FromSampler(int binding, SamplerHandle sampler, int arrayElement = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(binding);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayElement);
        if (sampler.IsNull)
        {
            throw new ArgumentException("The sampler is the null handle.", nameof(sampler));
        }

        return new(BindGroupEntryKind.Sampler, binding, arrayElement, default, default, sampler);
    }

    /// <summary>
    /// Returns the kind, binding, array element and the one resource the kind names, for example
    /// <c>BindGroupEntry { Kind = Texture, Binding = 1, ArrayElement = 0, View = TextureView(3:1) }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString()
    {
        string resource = Kind switch
        {
            BindGroupEntryKind.Buffer => "Buffer = " + _buffer.ToString(),
            BindGroupEntryKind.Texture => "View = " + _view.ToString(),
            _ => "Sampler = " + _sampler.ToString(),
        };
        return string.Create(CultureInfo.InvariantCulture, $"BindGroupEntry {{ Kind = {Kind}, Binding = {Binding}, ArrayElement = {ArrayElement}, {resource} }}");
    }

    private InvalidOperationException WrongKind(BindGroupEntryKind wanted) => new(string.Create(
        CultureInfo.InvariantCulture, $"The bind group entry at binding {Binding} is a {Kind} entry, not a {wanted} entry."));
}
