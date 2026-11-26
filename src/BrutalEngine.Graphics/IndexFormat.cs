using System.Diagnostics.CodeAnalysis;

namespace BrutalEngine.Graphics;

/// <summary>The size of each index in an index buffer (docs/design/graphics.md section 9).</summary>
/// <remarks>Backends map each value to their native one by name, never by casting.</remarks>
public enum IndexFormat
{
    /// <summary>Unsigned 16-bit indices. The default value.</summary>
    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "The design note's name for the format, as WebGPU's uint16 index format: it names the index size.")]
    Uint16 = 0,

    /// <summary>Unsigned 32-bit indices.</summary>
    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "The design note's name for the format, as WebGPU's uint32 index format: it names the index size.")]
    Uint32 = 1,
}
