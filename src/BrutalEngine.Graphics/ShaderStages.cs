using System;

namespace BrutalEngine.Graphics;

/// <summary>A set of shader stages, combined with <c>|</c>: the stages that see a binding or push constants, or that a transition orders.</summary>
/// <remarks>
/// Used by push constant ranges (docs/design/graphics.md section 7), bind group layout entries (section 8) and the
/// backend interface's resource transitions. Backends map each flag to their native stage by name, never by casting.
/// </remarks>
[Flags]
public enum ShaderStages
{
    /// <summary>No stages. The default value.</summary>
    None = 0,

    /// <summary>The vertex stage.</summary>
    Vertex = 1 << 0,

    /// <summary>The fragment stage.</summary>
    Fragment = 1 << 1,

    /// <summary>The compute stage.</summary>
    Compute = 1 << 2,
}
