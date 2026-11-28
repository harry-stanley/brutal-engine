namespace BrutalEngine.Graphics;

/// <summary>The bytecode format of one <see cref="ShaderCode"/> in a shader module.</summary>
/// <remarks>
/// A module can carry its code in several formats, and each backend picks the one it consumes (docs/design/graphics.md
/// section 7). Later backends add their formats here; a module with no format the backend consumes is the device's
/// <c>Unsupported</c> result.
/// </remarks>
public enum ShaderCodeFormat
{
    /// <summary>SPIR-V, as the offline cooker emits it: a whole number of 32-bit words in the host's byte order (little-endian on every supported target), the first being the magic number <c>0x07230203</c>. The Vulkan backend's format. The default value.</summary>
    SpirV = 0,
}
