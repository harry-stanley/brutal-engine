using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BrutalEngine.Core.Logging;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class PlatformOptionsTests
{
    [Fact]
    public void Defaults_MatchDesign()
    {
        PlatformOptions options = new() { Organization = "Ahwoo", Application = "Game" };

        Assert.Equal("Ahwoo", options.Organization);
        Assert.Equal("Game", options.Application);
        Assert.Equal(HeadlessMode.None, options.Headless);
        Assert.Equal(1024, options.EventCapacity);
        Assert.Equal(8192, options.TextCapacityBytes);
        Assert.Same(TimeProvider.System, options.Clock);
        Assert.Null(options.UserDataDirectory);
        Assert.Null(options.Logger);
        Assert.Equal(
            [
                "Application", "Clock", "EventCapacity", "Headless", "Logger", "Organization", "TextCapacityBytes",
                "UserDataDirectory",
            ],
            typeof(PlatformOptions).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RequiredMembers_AreOrganizationAndApplication()
    {
        string[] required = typeof(PlatformOptions).GetProperties()
            .Where(property => property.IsDefined(typeof(RequiredMemberAttribute)))
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Application", "Organization"], required);
        Assert.True(typeof(PlatformOptions).IsDefined(typeof(RequiredMemberAttribute)));
    }

    [Fact]
    public void Properties_AreInitOnly()
    {
        foreach (PropertyInfo property in typeof(PlatformOptions).GetProperties())
        {
            MethodInfo? setter = property.SetMethod;
            Assert.NotNull(setter);
            Assert.Contains(typeof(IsExternalInit), setter.ReturnParameter.GetRequiredCustomModifiers());
        }
    }

    [Fact]
    public void With_ChangesOnlyNamedProperties()
    {
        PlatformOptions options = new() { Organization = "Ahwoo", Application = "Game" };
        using Logger logger = new(new LoggerOptions { Sinks = [] });

        PlatformOptions changed = options with { EventCapacity = 64, Logger = logger };

        Assert.Equal(64, changed.EventCapacity);
        Assert.Same(logger, changed.Logger);
        Assert.NotEqual(options, changed);
        Assert.Equal(options, changed with { EventCapacity = 1024, Logger = null });
    }
}
