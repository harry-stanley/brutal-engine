using System;

namespace BrutalEngine.Graphics;

/// <summary>Describes a shader module: compiled code that pipelines name entry points in.</summary>
/// <remarks>
/// <para>
/// The runtime never compiles shader source: <see cref="Code"/> holds bytecode cooked offline, in one or more formats, and
/// the backend picks the format it consumes (docs/design/graphics.md section 7). A module with no format the backend
/// consumes is the device's <c>Unsupported</c> result. Two entries in the same format, or SPIR-V that fails the
/// structural check, throw <see cref="ArgumentException"/>, because cooked bytecode that fails them is a build error; a
/// format outside <see cref="ShaderCodeFormat"/> throws <see cref="ArgumentOutOfRangeException"/>. A driver rejecting
/// valid-looking code is the device's <c>InvalidData</c> result.
/// </para>
/// <para>
/// An immutable value: safe to copy and to use from any thread. Equality compares <see cref="Code"/> by reference, as
/// <see cref="ReadOnlyMemory{T}"/> does, and the debug name ordinally. The device keeps no reference to the description,
/// its code or its <see cref="DebugName"/>. No member allocates, except <see cref="object.ToString"/>.
/// </para>
/// </remarks>
public readonly record struct ShaderModuleDescription
{
    /// <summary>The module's code, at most one entry per format; the backend uses the entry in its own format.</summary>
    public required ReadOnlyMemory<ShaderCode> Code { get; init; }

    /// <summary>
    /// An optional name for debuggers, validation messages, errors and the leak report (docs/design/graphics.md
    /// section 13); <see langword="null"/> for none.
    /// </summary>
    public string? DebugName { get; init; }
}
