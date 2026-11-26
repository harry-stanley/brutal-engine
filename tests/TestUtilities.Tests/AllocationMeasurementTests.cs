using System;
using System.Runtime;
using Xunit;

namespace BrutalEngine.TestUtilities.Tests;

/// <summary>
/// Checks that this test host measures allocations exactly, so the zero-allocation tests can't fail because of
/// background collections that other tests cause (docs/05-testing-strategy.md, "Allocation tests").
/// </summary>
public sealed class AllocationMeasurementTests
{
    [Fact]
    public void TestHost_BackgroundGarbageCollection_IsDisabled()
    {
        // The switch the project's ConcurrentGarbageCollection property writes, and the collector's effective mode
        // (Interactive with background collection on, Batch with it off), as BrutalEngine.Core.Tests checks.
        Assert.Equal("false", AppContext.GetData("System.GC.Concurrent") as string, ignoreCase: true);
        Assert.Equal(GCLatencyMode.Batch, GCSettings.LatencyMode);
    }
}
