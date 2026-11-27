using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using BrutalEngine.Cook.Shaders;

namespace BrutalEngine.Cook.Cli;

/// <summary>
/// Picks the verb (the first argument) and runs it with the remaining arguments. New verbs are added to
/// the verb table; a verb owns its own arguments, usage text and help.
/// </summary>
internal static class Dispatcher
{
    /// <summary>The top-level usage text, printed for <c>--help</c> and after a verb error.</summary>
    public const string Usage = """
        Usage: brutal-cook <verb> [arguments]

        Verbs:
          shader    Compile a Slang source to SPIR-V, one file per entry point.

        'brutal-cook <verb> --help' describes a verb's arguments.
        """;

    // Immutable after the type initializer: verb name (exact, ordinal) to its entry point.
    private static readonly FrozenDictionary<string, CookVerb> _verbs =
        new Dictionary<string, CookVerb>(StringComparer.Ordinal) { ["shader"] = ShaderCommand.Run }
            .ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Runs a brutal-cook command line.</summary>
    /// <param name="args">The process arguments, verb first.</param>
    /// <param name="stdout">Receives help and progress output.</param>
    /// <param name="stderr">Receives errors, diagnostics and usage after a usage error.</param>
    /// <returns>
    /// The verb's exit code; <see cref="CookExitCode.Success"/> for <c>brutal-cook --help</c> alone; otherwise, for no
    /// verb or an unknown one (<c>--help</c> followed by anything included), <see cref="CookExitCode.Usage"/>.
    /// </returns>
    public static CookExitCode Run(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr)
    {
        if (args is ["--help"])
        {
            stdout.WriteLine(Usage);
            return CookExitCode.Success;
        }

        if (args.Length > 0 && _verbs.TryGetValue(args[0], out CookVerb? verb))
        {
            return verb(args[1..], stdout, stderr);
        }

        stderr.WriteLine(args.Length == 0 ? "brutal-cook: no verb given." : $"brutal-cook: unknown verb '{args[0]}'.");
        stderr.WriteLine(Usage);
        return CookExitCode.Usage;
    }
}
