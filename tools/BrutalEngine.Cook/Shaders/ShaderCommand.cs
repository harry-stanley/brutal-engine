using System;
using System.IO;
using BrutalEngine.Cook.Cli;

namespace BrutalEngine.Cook.Shaders;

/// <summary>
/// The <c>shader</c> verb: compiles one Slang source to one SPIR-V file per entry point,
/// <c>&lt;output-dir&gt;/&lt;source file name without extension&gt;.&lt;entry&gt;.spv</c>, overwriting existing files.
/// </summary>
internal static class ShaderCommand
{
    /// <summary>The verb's usage text, printed for <c>--help</c> and after a usage error.</summary>
    public const string Usage = """
        Usage: brutal-cook shader <source.slang> --entry <name>:<vertex|fragment|compute> [--entry ...]
                                  --output-dir <dir> [--matrix-layout column-major|row-major]

        Compiles each entry point of the Slang source to SPIR-V and writes <dir>/<source name>.<entry>.spv.
        Nothing is written unless every entry point compiles. The default matrix layout is column-major.
        Exit codes: 0 success, 2 compile errors, 3 source not found, 4 output not writable,
        5 Slang failed to load, 64 bad command line.
        """;

    /// <summary>Runs the verb with the real Slang runtime.</summary>
    /// <param name="args">The arguments after <c>shader</c>.</param>
    /// <param name="stdout">Receives help and the path of each file written.</param>
    /// <param name="stderr">Receives errors and Slang's diagnostics.</param>
    /// <returns>The exit code (see <see cref="CookExitCode"/>).</returns>
    public static CookExitCode Run(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr) =>
        Run(args, stdout, stderr, new SlangRuntime());

    /// <summary>Runs the verb with the given Slang runtime (the tests' seam).</summary>
    /// <param name="args">The arguments after <c>shader</c>.</param>
    /// <param name="stdout">Receives help and the path of each file written.</param>
    /// <param name="stderr">Receives errors and Slang's diagnostics.</param>
    /// <param name="runtime">Creates and releases the Slang global session.</param>
    /// <returns>
    /// <see cref="CookExitCode.Success"/> after <c>--help</c> (anywhere) or once every file is written;
    /// <see cref="CookExitCode.Usage"/> for a bad command line; <see cref="CookExitCode.InputNotFound"/> when the
    /// source isn't a file; otherwise the compiler's code, or <see cref="CookExitCode.OutputFailed"/> when the
    /// directory can't be created or a file can't be written (files written before the failure stay).
    /// </returns>
    public static CookExitCode Run(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr, ISlangRuntime runtime)
    {
        if (args.Contains("--help"))
        {
            stdout.WriteLine(Usage);
            return CookExitCode.Success;
        }

        if (!CookCommandLine.TryParseShader(args, out ShaderCookOptions? options, out string? error))
        {
            stderr.WriteLine($"brutal-cook shader: {error}");
            stderr.WriteLine(Usage);
            return CookExitCode.Usage;
        }

        if (!File.Exists(options.Source))
        {
            stderr.WriteLine($"brutal-cook shader: source file '{options.Source}' not found.");
            return CookExitCode.InputNotFound;
        }

        CookExitCode compiled = new SlangShaderCompiler(runtime).Compile(options, stderr, out byte[][] code);
        return compiled == CookExitCode.Success ? Write(options, code, stdout, stderr) : compiled;
    }

    private static CookExitCode Write(ShaderCookOptions options, byte[][] code, TextWriter stdout, TextWriter stderr)
    {
        string stem = Path.GetFileNameWithoutExtension(options.Source);
        try
        {
            Directory.CreateDirectory(options.OutputDirectory);
            for (int i = 0; i < code.Length; i++)
            {
                string path = Path.Combine(options.OutputDirectory, $"{stem}.{options.Entries[i].Name}.spv");
                File.WriteAllBytes(path, code[i]);
                stdout.WriteLine(path);
            }

            return CookExitCode.Success;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"brutal-cook shader: can't write to '{options.OutputDirectory}': {e.Message}");
            return CookExitCode.OutputFailed;
        }
    }
}
