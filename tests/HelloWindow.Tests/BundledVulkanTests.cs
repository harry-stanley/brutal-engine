using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace HelloWindow.Tests;

public sealed class BundledVulkanTests
{
    // TryConfigure needs a fully qualified directory, and what that looks like depends on the OS running the tests.
    private static readonly string _appDirectory = OperatingSystem.IsWindows() ? @"C:\Games\HelloWindow" : "/Applications/HelloWindow";
    private static readonly string _loader = Path.Join(_appDirectory, "libvulkan.1.dylib");
    private static readonly string _icd = Path.Join(_appDirectory, "MoltenVK_icd.json");

    [Fact]
    public void TryConfigure_LoaderAndIcdPresent_PreloadsAndSetsDriverFiles()
    {
        var host = new FakeHost(isMacOS: true, _loader, _icd);

        Assert.True(BundledVulkan.TryConfigure(_appDirectory, explicitLoader: null, host));

        // The driver is selected first: the loader reads VK_DRIVER_FILES when it enumerates drivers.
        Assert.Equal([$"set VK_DRIVER_FILES={_icd}", $"preload {_loader}"], host.Calls);
        Assert.Equal(_icd, host.Environment["VK_DRIVER_FILES"]);
    }

    [Fact]
    public void TryConfigure_DriverFilesAlreadySet_LeavesItAlone()
    {
        const string userIcd = "/opt/vulkan/icd.d/custom_icd.json";
        var host = new FakeHost(isMacOS: true, _loader, _icd);
        host.Environment["VK_DRIVER_FILES"] = userIcd;

        Assert.True(BundledVulkan.TryConfigure(_appDirectory, explicitLoader: null, host));

        Assert.Equal([$"preload {_loader}"], host.Calls);
        Assert.Equal(userIcd, host.Environment["VK_DRIVER_FILES"]);
    }

    [Fact]
    public void TryConfigure_LegacyIcdFilenamesSet_LeavesDriverFilesAlone()
    {
        var host = new FakeHost(isMacOS: true, _loader, _icd);
        host.Environment["VK_ICD_FILENAMES"] = "/opt/vulkan/icd.d/custom_icd.json";

        Assert.True(BundledVulkan.TryConfigure(_appDirectory, explicitLoader: null, host));

        Assert.Equal([$"preload {_loader}"], host.Calls);
        Assert.False(host.Environment.ContainsKey("VK_DRIVER_FILES"));
    }

    [Fact]
    public void TryConfigure_DriverFilesEmpty_TreatsItAsUnset()
    {
        var host = new FakeHost(isMacOS: true, _icd);
        host.Environment["VK_DRIVER_FILES"] = string.Empty;

        Assert.True(BundledVulkan.TryConfigure(_appDirectory, explicitLoader: null, host));

        Assert.Equal([$"set VK_DRIVER_FILES={_icd}"], host.Calls);
    }

    [Fact]
    public void TryConfigure_NonMacOs_IsNoOp()
    {
        var host = new FakeHost(isMacOS: false, _loader, _icd);

        Assert.False(BundledVulkan.TryConfigure(_appDirectory, explicitLoader: null, host));

        Assert.Empty(host.Calls);
        Assert.Empty(host.Probed);
        Assert.Empty(host.Environment);
    }

    [Fact]
    public void TryConfigure_ExplicitLoader_DoesNotPreloadTheBundledLoader()
    {
        var host = new FakeHost(isMacOS: true, _loader, _icd);

        Assert.True(BundledVulkan.TryConfigure(_appDirectory, explicitLoader: "/opt/vulkan/lib/libvulkan.1.dylib", host));

        // The caller preloads the explicit loader. The bundled driver is still the one to use with it.
        Assert.Equal([$"set VK_DRIVER_FILES={_icd}"], host.Calls);
    }

    [Fact]
    public void TryConfigure_OnlyLoaderBundled_PreloadsWithoutSettingDriverFiles()
    {
        var host = new FakeHost(isMacOS: true, _loader);

        Assert.True(BundledVulkan.TryConfigure(_appDirectory, explicitLoader: null, host));

        Assert.Equal([$"preload {_loader}"], host.Calls);
        Assert.False(host.Environment.ContainsKey("VK_DRIVER_FILES"));
    }

    [Fact]
    public void TryConfigure_NothingBundled_ReturnsFalseAndChangesNothing()
    {
        var host = new FakeHost(isMacOS: true);

        Assert.False(BundledVulkan.TryConfigure(_appDirectory, explicitLoader: null, host));

        Assert.Empty(host.Calls);
        Assert.Equal([_icd, _loader], host.Probed);
    }

    [Fact]
    public void TryConfigure_ProbesOnlyTheAppDirectory()
    {
        var host = new FakeHost(isMacOS: true);

        _ = BundledVulkan.TryConfigure(_appDirectory, explicitLoader: null, host);

        Assert.All(host.Probed, path => Assert.Equal(_appDirectory, Path.GetDirectoryName(path)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("HelloWindow")]
    [InlineData("./HelloWindow")]
    public void TryConfigure_AppDirectoryNotFullyQualified_Throws(string appDirectory)
    {
        var host = new FakeHost(isMacOS: true, _loader, _icd);

        Assert.ThrowsAny<ArgumentException>(() => BundledVulkan.TryConfigure(appDirectory, explicitLoader: null, host));

        Assert.Empty(host.Calls);
    }

    [Fact]
    public void TryConfigure_NullHost_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => BundledVulkan.TryConfigure<FakeHost>(_appDirectory, explicitLoader: null, null!));
    }

    // Records every side effect, in order, so tests can check what was done and in which order.
    private sealed class FakeHost(bool isMacOS, params string[] existingFiles) : BundledVulkan.IHost
    {
        private readonly HashSet<string> _files = new(existingFiles, StringComparer.Ordinal);

        public bool IsMacOS { get; } = isMacOS;

        public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

        public List<string> Calls { get; } = [];

        public List<string> Probed { get; } = [];

        public bool FileExists(string path)
        {
            Probed.Add(path);
            return _files.Contains(path);
        }

        public string? GetEnvironmentVariable(string name) => Environment.GetValueOrDefault(name);

        public void SetEnvironmentVariable(string name, string value)
        {
            Calls.Add($"set {name}={value}");
            Environment[name] = value;
        }

        public void PreloadLoader(string path) => Calls.Add($"preload {path}");
    }
}
