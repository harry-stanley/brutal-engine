using System;
using System.Runtime.InteropServices;

namespace HelloWindow;

/// <summary>Entry point. Exit codes are listed in <see cref="ExitCode"/>.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        SampleConsole.Info($"HelloWindow on {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
        if (!HelloWindowOptions.TryParse(args, out HelloWindowOptions? options, out string error))
        {
            SampleConsole.Error(error);
            SampleConsole.Error(HelloWindowOptions.Usage);
            return (int)ExitCode.Usage;
        }

        return (int)HelloWindowApp.Run(options);
    }
}
