using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

/// <summary>
/// Every test that calls SDL: SDL's state (initialisation, hints, environment, error text) is process-wide, so these
/// never run in parallel with each other (docs/design/platform.md section 9). No fixture keeps SDL alive.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SdlNativeTests
{
    public const string Name = "SDL";
}
