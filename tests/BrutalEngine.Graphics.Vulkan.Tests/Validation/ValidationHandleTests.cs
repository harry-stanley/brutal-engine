using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Validation;

/// <summary>
/// Tests that send messages to a released sink handle. CoreCLR reuses a freed handle's slot at once, so these never run in
/// parallel with tests that create sinks (as BrutalEngine.Platform.Sdl3.Tests' SdlNativeTests does for SDL).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ValidationHandleTests
{
    public const string Name = "Validation handles";
}
