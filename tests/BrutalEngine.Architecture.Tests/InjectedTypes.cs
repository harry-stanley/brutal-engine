using System;

namespace BrutalEngine.Architecture.Tests;

/// <summary>
/// Types that tooling injects into engine assemblies at test time. They aren't engine code, so the type-level rules
/// skip them.
/// </summary>
/// <remarks>
/// Only exact, documented injectors are listed, so a real engine type can never be excused by accident:
/// Microsoft.CodeCoverage's static instrumentation (used for <c>--coverage</c> on Linux and macOS) adds
/// <c>Microsoft.CodeCoverage.Instrumentation.Static.Tracker.StaticManagedTrackerTemplate_&lt;guid&gt;</c>, which has
/// static mutable fields. Engine types live in <c>BrutalEngine.*</c> namespaces, so they can't match.
/// </remarks>
internal static class InjectedTypes
{
    private const string CodeCoverageNamespace = "Microsoft.CodeCoverage";

    /// <summary>True if <paramref name="type"/> was injected by tooling rather than written in the engine.</summary>
    public static bool IsInjected(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        string? ns = type.Namespace;
        return ns is not null
            && (ns == CodeCoverageNamespace || ns.StartsWith(CodeCoverageNamespace + ".", StringComparison.Ordinal));
    }
}
