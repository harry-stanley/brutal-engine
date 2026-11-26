using System;
using System.IO;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Loader;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Loader;

public sealed class LoaderSetupTests
{
    private const nint SystemEntry = 0x1000;
    private const nint BundledEntry = 0x2000;
    private const nint ExplicitEntry = 0x3000;
    private const string UserDrivers = "/user/drivers.json";

    // The fake host never touches the disk; the paths only need to be fully qualified on the OS running the tests.
    private static readonly string _app = Path.Join(Path.GetTempPath(), "loader-app");
    private static readonly string _icd = Path.Join(_app, LoaderSetup.IcdFileName);
    private static readonly string _bundled = Path.Join(_app, LoaderSetup.LoaderFileName);
    private static readonly string _explicit = Path.Join(_app, "sdk", "vulkan-explicit");
    private static readonly string _setIcd = $"setenv {LoaderSetup.DriverFilesVariable}={_icd}";
    private static readonly EngineError _setenvFailure = new(ErrorKind.Native, "fake: setenv failed", 12);

    [Theory]
    [InlineData("system: Windows and Linux use BRUTAL's search")]
    [InlineData("system: bundled files are ignored off macOS")]
    [InlineData("system: no loader found")]
    [InlineData("explicit: off macOS, drivers untouched")]
    [InlineData("explicit: on macOS, wins over the bundled loader, bundled driver set")]
    [InlineData("explicit: preload failure is passed through")]
    [InlineData("bundled: driver set first, then the loader preloaded")]
    [InlineData("bundled: VK_DRIVER_FILES already set is kept")]
    [InlineData("bundled: VK_ICD_FILENAMES already set is kept")]
    [InlineData("bundled: an empty VK_DRIVER_FILES counts as unset")]
    [InlineData("bundled: loader without a manifest")]
    [InlineData("bundled: manifest without a loader uses BRUTAL's search")]
    [InlineData("bundled: no bundled files uses BRUTAL's search")]
    [InlineData("bundled: no bundled files and no loader found")]
    [InlineData("bundled: setenv failure stops before the preload")]
    [InlineData("bundled: preload failure is passed through")]
    public void Table(string name)
    {
        LoaderCase c = Case(name);

        Result<LoaderDecision> result = LoaderSetup.Configure(_app, c.ExplicitLoader, c.Host);

        if (c.Decision is { } decision)
        {
            Assert.True(result.IsSuccess, result.ToString());
            Assert.Equal(decision, result.Value);
        }
        else
        {
            Assert.True(result.IsFailure, result.ToString());
            Assert.Equal(c.Error, result.Error);
        }

        Assert.Equal(c.Effects, c.Host.Effects);
    }

    [Fact]
    public void Mismatch_IsUnavailableNamingPath()
    {
        // A second setup naming another loader than the one BRUTAL already bound for the process.
        FakeLoaderHost host = Host(macOS: false);
        Assert.True(LoaderSetup.Configure(_app, _bundled, host).IsSuccess);

        Result<LoaderDecision> result = LoaderSetup.Configure(_app, _explicit, host);

        Assert.True(result.IsFailure);
        Assert.Equal(new EngineError(ErrorKind.Unavailable, "BRUTAL resolved a different Vulkan loader than " + _explicit), result.Error);
    }

    [Fact]
    public void Mismatch_BrutalBoundBeforePreload_IsUnavailable()
    {
        FakeLoaderHost host = Host(macOS: true, _bundled);
        Assert.Equal(SystemEntry, host.BrutalGetInstanceProcAddr());

        Result<LoaderDecision> result = LoaderSetup.Configure(_app, null, host);

        Assert.Equal(new EngineError(ErrorKind.Unavailable, "BRUTAL resolved a different Vulkan loader than " + _bundled), result.Error);
    }

    [Fact]
    public void SecondCall_Idempotent()
    {
        FakeLoaderHost host = Host(macOS: true, _icd, _bundled);

        LoaderDecision first = LoaderSetup.Configure(_app, null, host).Value;
        LoaderDecision second = LoaderSetup.Configure(_app, null, host).Value;

        Assert.Equal(new LoaderDecision(_bundled, LoaderOrigin.Bundled, _icd), first);
        Assert.Equal(first, second);
        Assert.Equal([_setIcd, "preload " + _bundled, "preload " + _bundled], host.Effects);
        Assert.Equal(_icd, host.Environment[LoaderSetup.DriverFilesVariable]);
    }

    [Fact]
    public void Configure_NullAppDirectory_Throws()
    {
        Assert.Throws<ArgumentNullException>("appDirectory", () => LoaderSetup.Configure(null!, null, Host(macOS: false)));
    }

    [Fact]
    public void Configure_NullHost_Throws()
    {
        Assert.Throws<ArgumentNullException>("host", () => LoaderSetup.Configure<FakeLoaderHost>(_app, null, null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/app")]
    public void Configure_AppDirectoryNotFullyQualified_Throws(string appDirectory)
    {
        FakeLoaderHost host = Host(macOS: true, _icd);

        Assert.Throws<ArgumentException>(nameof(appDirectory), () => LoaderSetup.Configure(appDirectory, null, host));
        Assert.Empty(host.Effects);
    }

    [Theory]
    [InlineData("")]
    [InlineData("vulkan-1.dll")]
    public void Configure_ExplicitLoaderNotFullyQualified_Throws(string explicitLoader)
    {
        FakeLoaderHost host = Host(macOS: true, _icd);

        Assert.Throws<ArgumentException>(nameof(explicitLoader), () => LoaderSetup.Configure(_app, explicitLoader, host));
        Assert.Empty(host.Effects);
    }

    private static LoaderCase Case(string name) => name switch
    {
        "system: Windows and Linux use BRUTAL's search" =>
            new(Host(macOS: false), null, new(null, LoaderOrigin.System, null), null, []),
        "system: bundled files are ignored off macOS" =>
            new(Host(macOS: false, _icd, _bundled), null, new(null, LoaderOrigin.System, null), null, []),
        "system: no loader found" =>
            new(Host(macOS: false).WithoutSystemLoader(), null, null, new(ErrorKind.Unavailable, LoaderSetup.SystemLoaderMissingMessage), []),
        "explicit: off macOS, drivers untouched" =>
            new(Host(macOS: false, _icd), _explicit, new(_explicit, LoaderOrigin.Explicit, null), null, ["preload " + _explicit]),
        "explicit: on macOS, wins over the bundled loader, bundled driver set" =>
            new(Host(macOS: true, _icd, _bundled), _explicit, new(_explicit, LoaderOrigin.Explicit, _icd), null, [_setIcd, "preload " + _explicit]),
        "explicit: preload failure is passed through" =>
            new(Host(macOS: false).WithoutLibraries(), _explicit, null, FakeLoaderHost.PreloadFailure(_explicit), ["preload " + _explicit]),
        "bundled: driver set first, then the loader preloaded" =>
            new(Host(macOS: true, _icd, _bundled), null, new(_bundled, LoaderOrigin.Bundled, _icd), null, [_setIcd, "preload " + _bundled]),
        "bundled: VK_DRIVER_FILES already set is kept" =>
            new(Host(macOS: true, _icd, _bundled).WithEnvironment(LoaderSetup.DriverFilesVariable, UserDrivers), null,
                new(_bundled, LoaderOrigin.Bundled, UserDrivers), null, ["preload " + _bundled]),
        "bundled: VK_ICD_FILENAMES already set is kept" =>
            new(Host(macOS: true, _icd, _bundled).WithEnvironment(LoaderSetup.LegacyDriverFilesVariable, UserDrivers), null,
                new(_bundled, LoaderOrigin.Bundled, UserDrivers), null, ["preload " + _bundled]),
        "bundled: an empty VK_DRIVER_FILES counts as unset" =>
            new(Host(macOS: true, _icd, _bundled).WithEnvironment(LoaderSetup.DriverFilesVariable, ""), null,
                new(_bundled, LoaderOrigin.Bundled, _icd), null, [_setIcd, "preload " + _bundled]),
        "bundled: loader without a manifest" =>
            new(Host(macOS: true, _bundled), null, new(_bundled, LoaderOrigin.Bundled, null), null, ["preload " + _bundled]),
        "bundled: manifest without a loader uses BRUTAL's search" =>
            new(Host(macOS: true, _icd), null, new(null, LoaderOrigin.System, _icd), null, [_setIcd]),
        "bundled: no bundled files uses BRUTAL's search" =>
            new(Host(macOS: true), null, new(null, LoaderOrigin.System, null), null, []),
        "bundled: no bundled files and no loader found" =>
            new(Host(macOS: true).WithoutSystemLoader(), null, null, new(ErrorKind.Unavailable, LoaderSetup.SystemLoaderMissingMessage), []),
        "bundled: setenv failure stops before the preload" =>
            new(Host(macOS: true, _icd, _bundled).WithSetenvFailure(_setenvFailure), null, null, _setenvFailure, [_setIcd]),
        "bundled: preload failure is passed through" =>
            new(Host(macOS: true, _icd, _bundled).WithoutLibraries(), null, null, FakeLoaderHost.PreloadFailure(_bundled),
                [_setIcd, "preload " + _bundled]),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "No such case."),
    };

    private static FakeLoaderHost Host(bool macOS, params string[] files)
    {
        var host = new FakeLoaderHost { IsMacOS = macOS, SystemEntryPoint = SystemEntry };
        host.Files.UnionWith(files);
        host.Libraries[_bundled] = BundledEntry;
        host.Libraries[_explicit] = ExplicitEntry;
        return host;
    }

    private sealed record LoaderCase(FakeLoaderHost Host, string? ExplicitLoader, LoaderDecision? Decision, EngineError? Error, string[] Effects);
}
