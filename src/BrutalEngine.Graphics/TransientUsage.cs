using System;

namespace BrutalEngine.Graphics;

/// <summary>What a transient allocation will be bound as: one flag per kind of binding, combined with <c>|</c>.</summary>
/// <remarks>
/// <para>
/// A transient allocation is per-frame ring memory the CPU writes and the GPU reads in the same frame, without a copy
/// (docs/design/graphics.md section 6). The usage decides the allocation's offset alignment, for example
/// <see cref="GraphicsLimits.MinUniformBufferOffsetAlignment"/> for <see cref="Uniform"/>. An allocation needs at least
/// one flag: the value 0, <c>default(TransientUsage)</c>, has no member and is never valid.
/// </para>
/// <para>
/// The flags have the values of the matching <see cref="BufferUsage"/> flags, but the two types never convert to each
/// other: transient memory is never a copy source or destination. Backends map each flag to their native usage by
/// name, never by casting.
/// </para>
/// </remarks>
[Flags]
public enum TransientUsage
{
    /// <summary>Bound as a vertex buffer.</summary>
    Vertex = 1 << 0,

    /// <summary>Bound as an index buffer.</summary>
    Index = 1 << 1,

    /// <summary>Bound as a uniform buffer, in a frame bind group.</summary>
    Uniform = 1 << 2,

    /// <summary>Bound as a storage buffer, in a frame bind group.</summary>
    Storage = 1 << 3,

    /// <summary>The source of indirect draw or dispatch arguments.</summary>
    Indirect = 1 << 4,
}
