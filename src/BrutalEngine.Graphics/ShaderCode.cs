using System;

namespace BrutalEngine.Graphics;

/// <summary>A shader module's bytecode in one format.</summary>
/// <param name="Format">The bytecode's format.</param>
/// <param name="Bytes">
/// The bytecode, usually an embedded resource the offline cooker produced (docs/design/graphics.md section 7). The device
/// checks SPIR-V structurally when the module is created: a length that is a positive multiple of 4 and the magic number
/// <c>0x07230203</c> as the first word, read little-endian, else <see cref="ArgumentException"/>.
/// </param>
/// <remarks>
/// One entry of <see cref="ShaderModuleDescription.Code"/>, which may hold the same module in several formats. The device
/// reads the bytes only while it creates the module and keeps no reference to them.
/// An immutable value: safe to copy and to use from any thread. Equality compares <see cref="Bytes"/> by reference (the
/// same memory, offset and length), as <see cref="ReadOnlyMemory{T}"/> does, not by content. No member allocates, except
/// <see cref="object.ToString"/>.
/// </remarks>
public readonly record struct ShaderCode(ShaderCodeFormat Format, ReadOnlyMemory<byte> Bytes);
