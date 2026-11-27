using System.Collections.Generic;

namespace BrutalEngine.Cook.Shaders;

/// <summary>One Slang source to cook: a parsed <c>shader</c> command line, or one shader of a manifest.</summary>
/// <param name="Source">
/// The Slang source file: as given on the command line (relative paths resolve against the working directory), or
/// the full path a manifest resolves.
/// </param>
/// <param name="Entries">The entry points to compile, in command-line or manifest order: at least one, names unique ignoring case.</param>
/// <param name="OutputDirectory">The directory the SPIR-V files go to (a manifest's shader: its source's directory); created if missing.</param>
/// <param name="MatrixLayout">The default matrix layout.</param>
internal sealed record ShaderCookOptions(
    string Source,
    IReadOnlyList<ShaderEntry> Entries,
    string OutputDirectory,
    MatrixLayout MatrixLayout);
