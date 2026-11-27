namespace BrutalEngine.Cook.Shaders;

/// <summary>One entry point to compile: its function name in the source and its stage.</summary>
/// <param name="Name">The entry point's function name, an ASCII identifier; it also names the output file.</param>
/// <param name="Stage">The stage Slang compiles the entry point for.</param>
internal readonly record struct ShaderEntry(string Name, ShaderStage Stage);
