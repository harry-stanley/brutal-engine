using System.Collections.Generic;

namespace BrutalEngine.Cook.Shaders;

/// <summary>A parsed <c>shader</c> command line.</summary>
/// <param name="Source">The Slang source file, as given (relative paths resolve against the working directory).</param>
/// <param name="Entries">The entry points to compile, in command-line order: at least one, names unique ignoring case.</param>
/// <param name="OutputDirectory">The directory the SPIR-V files go to; created if missing.</param>
/// <param name="MatrixLayout">The default matrix layout.</param>
internal sealed record ShaderCookOptions(
    string Source,
    IReadOnlyList<ShaderEntry> Entries,
    string OutputDirectory,
    MatrixLayout MatrixLayout);
