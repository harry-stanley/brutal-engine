using System;
using BrutalEngine.Cook.Cli;

namespace BrutalEngine.Cook;

/// <summary>The brutal-cook entry point: hands the arguments to the verb dispatcher and returns its exit code.</summary>
internal static class Program
{
    private static int Main(string[] args) => (int)Dispatcher.Run(args, Console.Out, Console.Error);
}
