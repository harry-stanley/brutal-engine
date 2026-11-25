using System;

namespace HelloWindow;

/// <summary>The sample's diagnostics: progress to stdout, problems to stderr.</summary>
/// <remarks>TODO(#26): replace with the BrutalEngine.Core logger.</remarks>
internal static class SampleConsole
{
    /// <summary>Writes a progress line to stdout.</summary>
    /// <param name="message">The line.</param>
    public static void Info(string message) => Console.Out.WriteLine(message);

    /// <summary>Writes a problem to stderr.</summary>
    /// <param name="message">The line.</param>
    public static void Error(string message) => Console.Error.WriteLine(message);
}
