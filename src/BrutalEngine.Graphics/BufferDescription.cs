using System.Globalization;

namespace BrutalEngine.Graphics;

/// <summary>Describes a GPU buffer to create: its size, usage and memory intent.</summary>
/// <remarks>
/// <para>
/// The device validates a description before the backend sees it (docs/design/graphics.md section 5). A non-positive
/// <see cref="Size"/>, <see cref="BufferUsage.None"/>, a <see cref="MemoryIntent.Readback"/> buffer with any usage
/// other than <see cref="BufferUsage.CopyDestination"/>, or a usage or memory intent outside the enum's members is
/// structurally invalid and throws <see cref="System.ArgumentException"/>
/// (<see cref="System.ArgumentOutOfRangeException"/> for the size and undefined values). Running out of memory is a
/// <c>Result</c> failure.
/// </para>
/// <para>
/// An immutable value: safe to copy and to use from any thread. Equality compares every property; the debug name
/// compares ordinally. The device copies what it needs and keeps no reference to the description or its
/// <see cref="DebugName"/>. No member allocates, except <see cref="ToString"/>.
/// </para>
/// </remarks>
public readonly record struct BufferDescription
{
    /// <summary>The size in bytes; greater than 0.</summary>
    public required long Size { get; init; }

    /// <summary>How the buffer will be used; at least one flag.</summary>
    public required BufferUsage Usage { get; init; }

    /// <summary>What the CPU does with the buffer's memory. Defaults to <see cref="MemoryIntent.GpuOnly"/>.</summary>
    public MemoryIntent Memory { get; init; }

    /// <summary>
    /// An optional name for debuggers, validation messages, errors and the leak report (docs/design/graphics.md
    /// section 13); <see langword="null"/> for none.
    /// </summary>
    public string? DebugName { get; init; }

    /// <summary>
    /// Returns every property, for example
    /// <c>BufferDescription { Size = 65536, Usage = Vertex, CopyDestination, Memory = GpuOnly, DebugName = sprites }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"BufferDescription {{ Size = {Size}, Usage = {Usage}, Memory = {Memory}, DebugName = {DebugName} }}");
}
