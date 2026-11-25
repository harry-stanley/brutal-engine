using Brutal.FakeApi;

// Mirrors the tracker type that Microsoft.CodeCoverage's static instrumentation injects on Linux and macOS: static
// mutable fields (and here, a binding type in its public surface), in the tracker's real namespace.
#pragma warning disable CA1716, CA2211, IDE0130 // reflection-only fields; the namespace must match the injector's exactly

namespace Microsoft.CodeCoverage.Instrumentation.Static.Tracker;

public static class FakeStaticManagedTrackerTemplate
{
    public static string? TraceFile;

    public static byte[][]? Messages;

    public static FakeDevice Binding;
}
