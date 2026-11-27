using System;
using System.IO;

namespace BrutalEngine.Cook.Cli;

/// <summary>A verb's entry point, called by <see cref="Dispatcher"/> with the arguments after the verb.</summary>
/// <param name="args">The arguments after the verb name.</param>
/// <param name="stdout">Receives help and progress output.</param>
/// <param name="stderr">Receives errors, diagnostics and usage after a usage error.</param>
/// <returns>The process exit code.</returns>
internal delegate CookExitCode CookVerb(ReadOnlySpan<string> args, TextWriter stdout, TextWriter stderr);
