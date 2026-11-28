using System.Diagnostics.CodeAnalysis;

namespace BrutalEngine.Graphics;

/// <summary>The type and component count of one vertex attribute in a vertex buffer.</summary>
/// <remarks>
/// Each member's size in bytes is given on it; an attribute lies inside its buffer's stride (docs/design/graphics.md
/// section 7). Normalized and integer formats are read by the shader as floats in [0, 1] or as integers, as named.
/// </remarks>
public enum VertexFormat
{
    /// <summary>One 32-bit float: 4 bytes. The default value.</summary>
    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "The design note's name for the format, as WebGPU's float32 vertex format: it names the component type and count.")]
    Float32 = 0,

    /// <summary>Two 32-bit floats: 8 bytes.</summary>
    Float32x2 = 1,

    /// <summary>Three 32-bit floats: 12 bytes.</summary>
    Float32x3 = 2,

    /// <summary>Four 32-bit floats: 16 bytes.</summary>
    Float32x4 = 3,

    /// <summary>Two 16-bit floats: 4 bytes.</summary>
    Float16x2 = 4,

    /// <summary>Four 16-bit floats: 8 bytes.</summary>
    Float16x4 = 5,

    /// <summary>Four 8-bit unsigned normalized values, read as floats in [0, 1]: 4 bytes.</summary>
    Unorm8x4 = 6,

    /// <summary>Four 8-bit unsigned integers: 4 bytes.</summary>
    Uint8x4 = 7,

    /// <summary>One 32-bit unsigned integer: 4 bytes.</summary>
    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "The design note's name for the format, as WebGPU's uint32 vertex format: it names the component type and count.")]
    Uint32 = 8,

    /// <summary>Two 32-bit unsigned integers: 8 bytes.</summary>
    Uint32x2 = 9,

    /// <summary>One 32-bit signed integer: 4 bytes.</summary>
    Sint32 = 10,
}
