using System;
using System.Collections.Generic;
using System.IO;
using BrutalEngine.Cook.Cli;

namespace BrutalEngine.Cook.Shaders;

/// <summary>
/// The <c>shader</c> verb: compiles one Slang source to one SPIR-V file per entry point,
/// <c>&lt;output-dir&gt;/&lt;source file name without extension&gt;.&lt;entry&gt;.spv</c>, overwriting existing files.
/// Its manifest form cooks every shader that <c>shaders.cook.json</c> files list, next to each source, or with
/// <c>--check</c> compares the cooked code with the files already there.
/// </summary>
internal static class ShaderCommand
{
    /// <summary>The verb's usage text, printed for <c>--help</c> and after a usage error.</summary>
    public const string Usage = """
        Usage: brutal-cook shader <source.slang> --entry <name>:<vertex|fragment|compute> [--entry ...]
                                  --output-dir <dir> [--matrix-layout column-major|row-major]
               brutal-cook shader --manifest <shaders.cook.json> [--manifest ...] [--check]

        Compiles each entry point of the Slang source to SPIR-V and writes <dir>/<source name>.<entry>.spv.
        Nothing is written unless every entry point compiles. The default matrix layout is column-major.
        --manifest cooks every shader the manifest lists, next to its source. With --check, nothing is written:
        each file is compared with the cooked code, and every missing or different one is named.
        Exit codes: 0 success, 2 compile errors, 3 source or manifest not found, 4 output not writable,
        5 Slang failed to load, 6 a checked file is missing or differs, 64 bad command line or manifest.
        """;

    /// <summary>Runs the verb with the real Slang runtime.</summary>
    /// <param name="args">The arguments after <c>shader</c>.</param>
    /// <param name="stdout">Receives help, the path of each file written and, with <c>--check</c>, each matching file.</param>
    /// <param name="stderr">Receives errors, Slang's diagnostics and, with <c>--check</c>, each missing or different file.</param>
    /// <returns>The exit code (see <see cref="CookExitCode"/>).</returns>
    public static CookExitCode Run(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr) =>
        Run(args, stdout, stderr, new SlangRuntime());

    /// <summary>Runs the verb with the given Slang runtime (the tests' seam).</summary>
    /// <param name="args">The arguments after <c>shader</c>. <c>--manifest</c> or <c>--check</c> selects the manifest form.</param>
    /// <param name="stdout">Receives help, the path of each file written and, with <c>--check</c>, each matching file.</param>
    /// <param name="stderr">Receives errors, Slang's diagnostics and, with <c>--check</c>, each missing or different file.</param>
    /// <param name="runtime">Creates and releases the Slang global sessions (one per source).</param>
    /// <returns>
    /// <see cref="CookExitCode.Success"/> after <c>--help</c> (anywhere), once every file is written, or when every
    /// checked file matches; <see cref="CookExitCode.Usage"/> for a bad command line or manifest;
    /// <see cref="CookExitCode.InputNotFound"/> when a manifest can't be read or a source isn't a file (checked for every
    /// manifest and source before anything compiles); otherwise the first failing compile's code;
    /// <see cref="CookExitCode.OutputFailed"/> when a directory can't be created or a file can't be written (files
    /// written before the failure stay); or <see cref="CookExitCode.CheckFailed"/> when a checked file is missing,
    /// unreadable or different.
    /// </returns>
    public static CookExitCode Run(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr, ISlangRuntime runtime)
    {
        if (args.Contains("--help"))
        {
            stdout.WriteLine(Usage);
            return CookExitCode.Success;
        }

        if (args.Contains("--manifest") || args.Contains("--check"))
        {
            return CookCommandLine.TryParseShaderManifests(args, out ShaderManifestOptions? manifests, out string? manifestError)
                ? RunManifests(manifests, stdout, stderr, runtime)
                : UsageError(manifestError, stderr);
        }

        if (!CookCommandLine.TryParseShader(args, out ShaderCookOptions? options, out string? error))
        {
            return UsageError(error, stderr);
        }

        if (!SourceExists(options, stderr))
        {
            return CookExitCode.InputNotFound;
        }

        CookExitCode compiled = new SlangShaderCompiler(runtime).Compile(options, stderr, out byte[][] code);
        return compiled == CookExitCode.Success ? Write(options, code, stdout, stderr) : compiled;
    }

    private static CookExitCode UsageError(string error, TextWriter stderr)
    {
        stderr.WriteLine($"brutal-cook shader: {error}");
        stderr.WriteLine(Usage);
        return CookExitCode.Usage;
    }

    private static bool SourceExists(ShaderCookOptions options, TextWriter stderr)
    {
        if (File.Exists(options.Source))
        {
            return true;
        }

        stderr.WriteLine($"brutal-cook shader: source file '{options.Source}' not found.");
        return false;
    }

    private static CookExitCode RunManifests(ShaderManifestOptions options, TextWriter stdout, TextWriter stderr, ISlangRuntime runtime)
    {
        var shaders = new List<ShaderCookOptions>();
        foreach (string manifest in options.Manifests)
        {
            CookExitCode read = ReadManifest(manifest, shaders, stderr);
            if (read != CookExitCode.Success)
            {
                return read;
            }
        }

        if (!shaders.TrueForAll(shader => SourceExists(shader, stderr)))
        {
            return CookExitCode.InputNotFound;
        }

        var compiler = new SlangShaderCompiler(runtime);
        bool allMatch = true;
        foreach (ShaderCookOptions shader in shaders)
        {
            CookExitCode result = compiler.Compile(shader, stderr, out byte[][] code);
            if (result == CookExitCode.Success && options.Check)
            {
                allMatch = Matches(shader, code, stdout, stderr) && allMatch;
            }
            else if (result == CookExitCode.Success)
            {
                result = Write(shader, code, stdout, stderr);
            }

            if (result != CookExitCode.Success)
            {
                return result;
            }
        }

        return allMatch ? CookExitCode.Success : CookExitCode.CheckFailed;
    }

    private static CookExitCode ReadManifest(string manifest, List<ShaderCookOptions> shaders, TextWriter stderr)
    {
        byte[] json;
        try
        {
            json = File.ReadAllBytes(manifest);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"brutal-cook shader: can't read manifest '{manifest}': {e.Message}");
            return CookExitCode.InputNotFound;
        }

        if (!ShaderManifest.TryRead(manifest, json, out IReadOnlyList<ShaderCookOptions>? listed, out string? error))
        {
            stderr.WriteLine($"brutal-cook shader: {error}");
            return CookExitCode.Usage;
        }

        shaders.AddRange(listed);
        return CookExitCode.Success;
    }

    // Compares each entry's cooked code with its output file, writing nothing. True when every file matches.
    private static bool Matches(ShaderCookOptions options, byte[][] code, TextWriter stdout, TextWriter stderr)
    {
        bool allMatch = true;
        for (int i = 0; i < code.Length; i++)
        {
            string path = OutputPath(options, i);
            string? difference = Difference(path, code[i]);
            if (difference is null)
            {
                stdout.WriteLine($"matches: {path}");
            }
            else
            {
                stderr.WriteLine($"differs: {path} ({difference})");
                allMatch = false;
            }
        }

        return allMatch;
    }

    // Null when the file at `path` holds exactly `cooked`; otherwise how it differs.
    private static string? Difference(string path, byte[] cooked)
    {
        if (!File.Exists(path))
        {
            return $"missing; cooked {cooked.Length} bytes";
        }

        byte[] existing;
        try
        {
            existing = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"can't be read: {e.Message}";
        }

        int same = existing.AsSpan().CommonPrefixLength(cooked);
        return same == existing.Length && same == cooked.Length
            ? null
            : $"expected {existing.Length} bytes, cooked {cooked.Length} bytes, first difference at byte {same}";
    }

    private static CookExitCode Write(ShaderCookOptions options, byte[][] code, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            Directory.CreateDirectory(options.OutputDirectory);
            for (int i = 0; i < code.Length; i++)
            {
                string path = OutputPath(options, i);
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

    private static string OutputPath(ShaderCookOptions options, int entry) =>
        Path.Combine(options.OutputDirectory, $"{Path.GetFileNameWithoutExtension(options.Source)}.{options.Entries[entry].Name}.spv");
}
