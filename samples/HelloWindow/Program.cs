using System;
using System.Runtime.InteropServices;
using BrutalEngine.Core.Logging.Sinks;

namespace HelloWindow;

/// <summary>Entry point. Exit codes are listed in <see cref="ExitCode"/>.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        using var log = new SampleLog(new ConsoleLogSinkOptions());
        log.App.Information($"HelloWindow on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
        if (!HelloWindowOptions.TryParse(args, out HelloWindowOptions? options, out string error))
        {
            log.App.Error($"{error}\n{HelloWindowOptions.Usage}");
            return (int)ExitCode.Usage;
        }

        return (int)HelloWindowApp.Run(options, log);
    }
}
