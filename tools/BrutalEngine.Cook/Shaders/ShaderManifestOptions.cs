using System.Collections.Generic;

namespace BrutalEngine.Cook.Shaders;

/// <summary>A parsed <c>shader --manifest</c> command line.</summary>
/// <param name="Manifests">The manifest files, in command-line order, as given: at least one, none empty.</param>
/// <param name="Check">
/// True for <c>--check</c>: compile every listed shader in memory and compare the code with the files next to each
/// source, writing nothing.
/// </param>
internal sealed record ShaderManifestOptions(IReadOnlyList<string> Manifests, bool Check);
