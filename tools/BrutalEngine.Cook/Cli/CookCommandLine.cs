using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using BrutalEngine.Cook.Shaders;

namespace BrutalEngine.Cook.Cli;

/// <summary>
/// Parses verb arguments. Every token is matched exactly (ordinal), never through <c>Enum.TryParse</c>, which would
/// also accept other casings, numbers, comma lists and surrounding whitespace.
/// </summary>
internal static partial class CookCommandLine
{
    /// <summary>Parses the arguments of the <c>shader</c> verb (everything after <c>shader</c>).</summary>
    /// <param name="args">
    /// One source path and the options <c>--entry &lt;name&gt;:&lt;vertex|fragment|compute&gt;</c> (repeatable),
    /// <c>--output-dir &lt;dir&gt;</c> and <c>--matrix-layout column-major|row-major</c>, in any order.
    /// </param>
    /// <param name="options">The parsed command line on success; otherwise null.</param>
    /// <param name="error">On failure, one sentence naming the problem; otherwise null.</param>
    /// <returns>
    /// False for a missing, empty or second source; no <c>--entry</c>; an entry name that isn't an ASCII identifier
    /// or repeats another ignoring case (the names become file names, and file systems may ignore case); a stage
    /// other than exactly <c>vertex</c>, <c>fragment</c> or <c>compute</c>; a missing, empty or repeated
    /// <c>--output-dir</c>; a repeated or unknown <c>--matrix-layout</c> value; an option with no value; or an
    /// unknown option.
    /// </returns>
    public static bool TryParseShader(
        ReadOnlySpan<string> args,
        [NotNullWhen(true)] out ShaderCookOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        options = null;
        string? source = null;
        string? outputDirectory = null;
        string? layout = null;
        var entries = new List<ShaderEntry>();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith('-'))
            {
                error = Set(ref source, arg, "the source file");
            }
            else if (arg is not ("--entry" or "--output-dir" or "--matrix-layout"))
            {
                error = $"unknown option '{arg}'.";
            }
            else if (++i == args.Length)
            {
                error = $"{arg} needs a value.";
            }
            else
            {
                error = arg switch
                {
                    "--entry" => AddEntry(entries, args[i]),
                    "--output-dir" => Set(ref outputDirectory, args[i], "--output-dir"),
                    _ => Set(ref layout, args[i], "--matrix-layout"),
                };
            }

            if (error is not null)
            {
                return false;
            }
        }

        MatrixLayout? matrixLayout = layout is null ? MatrixLayout.ColumnMajor : ParseMatrixLayout(layout);
        error = source is null ? "no source file given."
            : entries.Count == 0 ? "at least one --entry is required."
            : outputDirectory is null ? "--output-dir is required."
            : matrixLayout is null ? $"unknown matrix layout '{layout}'; expected column-major or row-major."
            : null;
        if (error is not null)
        {
            return false;
        }

        options = new ShaderCookOptions(source!, entries, outputDirectory!, matrixLayout!.Value);
        return true;
    }

    /// <summary>Parses the arguments of the <c>shader</c> verb's manifest form (everything after <c>shader</c>).</summary>
    /// <param name="args"><c>--manifest &lt;file&gt;</c> (repeatable) and <c>--check</c>, in any order.</param>
    /// <param name="options">The parsed command line on success; otherwise null.</param>
    /// <param name="error">On failure, one sentence naming the problem; otherwise null.</param>
    /// <returns>
    /// False for no <c>--manifest</c>, a <c>--manifest</c> with no or an empty value, a repeated <c>--check</c>, or any
    /// other argument (a manifest names its own sources, entries, layouts and outputs).
    /// </returns>
    public static bool TryParseShaderManifests(
        ReadOnlySpan<string> args,
        [NotNullWhen(true)] out ShaderManifestOptions? options,
        [NotNullWhen(false)] out string? error)
    {
        options = null;
        var manifests = new List<string>();
        bool check = false;
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--check")
            {
                error = check ? "--check is given twice." : null;
                check = true;
            }
            else if (arg != "--manifest")
            {
                error = $"'{arg}' can't be used with --manifest or --check: a manifest names its own sources, entries and layouts.";
            }
            else if (++i == args.Length)
            {
                error = "--manifest needs a value.";
            }
            else
            {
                error = args[i].Length == 0 ? "--manifest is empty." : null;
                manifests.Add(args[i]);
            }

            if (error is not null)
            {
                return false;
            }
        }

        if (manifests.Count == 0)
        {
            error = "--check needs at least one --manifest.";
            return false;
        }

        options = new ShaderManifestOptions(manifests, check);
        error = null;
        return true;
    }

    /// <summary>Parses a stage token: exactly <c>vertex</c>, <c>fragment</c> or <c>compute</c> (ordinal).</summary>
    /// <param name="token">The token.</param>
    /// <returns>The stage, or null for any other token.</returns>
    public static ShaderStage? ParseStage(string token) => token switch
    {
        "vertex" => ShaderStage.Vertex,
        "fragment" => ShaderStage.Fragment,
        "compute" => ShaderStage.Compute,
        _ => null,
    };

    /// <summary>Parses a matrix layout token: exactly <c>column-major</c> or <c>row-major</c> (ordinal).</summary>
    /// <param name="token">The token.</param>
    /// <returns>The layout, or null for any other token.</returns>
    public static MatrixLayout? ParseMatrixLayout(string token) => token switch
    {
        "column-major" => MatrixLayout.ColumnMajor,
        "row-major" => MatrixLayout.RowMajor,
        _ => null,
    };

    /// <summary>Whether <paramref name="name"/> can name an entry point: an ASCII identifier, <c>[A-Za-z_][A-Za-z0-9_]*</c>.</summary>
    /// <param name="name">The name.</param>
    /// <returns>True for an ASCII identifier; it names an output file, so it can't hold path characters.</returns>
    public static bool IsEntryName(string name) => Identifier().IsMatch(name);

    private static string? Set(ref string? slot, string value, string what)
    {
        if (slot is not null)
        {
            return $"{what} is given twice.";
        }

        slot = value;
        return value.Length == 0 ? $"{what} is empty." : null;
    }

    private static string? AddEntry(List<ShaderEntry> entries, string value)
    {
        int colon = value.IndexOf(':', StringComparison.Ordinal);
        string name = colon < 0 ? value : value[..colon];
        ShaderStage? stage = colon < 0 ? null : ParseStage(value[(colon + 1)..]);
        if (!IsEntryName(name))
        {
            return $"--entry '{value}': the entry name must be an ASCII identifier (letters, digits and '_', not starting with a digit).";
        }

        if (stage is null)
        {
            return $"--entry '{value}': expected <name>:<vertex|fragment|compute>.";
        }

        if (entries.Exists(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"--entry '{name}' is given twice (names must differ ignoring case: each one names an output file).";
        }

        entries.Add(new ShaderEntry(name, stage.Value));
        return null;
    }

    // \z, not $: $ also matches before a final newline.
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\z")]
    private static partial Regex Identifier();
}
