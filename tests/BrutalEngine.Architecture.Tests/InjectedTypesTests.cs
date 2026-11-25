using System;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

public sealed class InjectedTypesTests
{
    [Fact]
    public void IsInjected_CodeCoverageTracker_IsTrue()
    {
        Assert.True(InjectedTypes.IsInjected(typeof(Microsoft.CodeCoverage.Instrumentation.Static.Tracker.FakeStaticManagedTrackerTemplate)));
    }

    [Theory]
    [InlineData(typeof(Fakes.StaticReferenceField))]
    [InlineData(typeof(InjectedTypes))]
    [InlineData(typeof(string))]
    public void IsInjected_OtherTypes_IsFalse(Type type)
    {
        Assert.False(InjectedTypes.IsInjected(type));
    }
}
