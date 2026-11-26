using System;
using System.IO;
using BrutalEngine.Core;
using BrutalEngine.Graphics.Vulkan.Loader;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Loader;

public sealed class ProcessLoaderHostTests
{
    [Fact]
    [Trait("Category", "Gpu")]
    public void SystemLoader_BindsBrutal()
    {
        // Windows and Linux with no explicit loader: BRUTAL's own search, nothing preloaded, drivers untouched.
        var host = default(ProcessLoaderHost);

        Result<LoaderDecision> result = LoaderSetup.Configure(AppContext.BaseDirectory, null, host);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(new LoaderDecision(null, LoaderOrigin.System, null), result.Value);
        Result<nint> bound = host.BrutalGetInstanceProcAddr();
        Assert.True(bound.IsSuccess, bound.ToString());
        Assert.NotEqual(0, bound.Value);
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void SystemLoader_SecondCall_AllocatesNothing()
    {
        // The real host is a struct passed to a generic: a box would show here (AC4). Limit: this proves it for optimised
        // code (Release, which CI runs) only. In optimised code the JIT folds a box feeding a null check, so removing the
        // value-type guard in LoaderSetup.Configure still passes here; it allocates 24 bytes only in Debug.
        var host = default(ProcessLoaderHost);
        string app = AppContext.BaseDirectory;
        Assert.True(LoaderSetup.Configure(app, null, host).IsSuccess);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Result<LoaderDecision> result = LoaderSetup.Configure(app, null, host);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result.IsSuccess);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void LoaderMissing_CarriesBrutalsMessage()
    {
        // What BRUTAL's static constructor throws when nothing loads (Brutal.Vulkan/ImportResolver.cs:69), as the runtime
        // wraps it.
        var failure = new TypeInitializationException(
            "Brutal.VulkanApi.Vulkan", new DllNotFoundException("Failed to load Vulkan library, tried: libvulkan.so.1, vulkan"));

        EngineError error = ProcessLoaderHost.LoaderMissing(failure);

        Assert.Equal(new EngineError(ErrorKind.Unavailable, "Vulkan loader missing: Failed to load Vulkan library, tried: libvulkan.so.1, vulkan"), error);
    }

    [Fact]
    public void LoaderMissing_NoInnerException_UsesOuterMessage()
    {
        var failure = new TypeInitializationException("Brutal.VulkanApi.Vulkan", null);

        EngineError error = ProcessLoaderHost.LoaderMissing(failure);

        Assert.Equal(new EngineError(ErrorKind.Unavailable, "Vulkan loader missing: " + failure.Message), error);
    }

    [Fact]
    public void LoaderMissing_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>("exception", () => ProcessLoaderHost.LoaderMissing(null!));
    }

    [Fact]
    public void Preload_MissingFile_IsUnavailableNamingPath()
    {
        string path = Path.Join(AppContext.BaseDirectory, "no-such-vulkan-loader");

        Result<nint> result = default(ProcessLoaderHost).Preload(path);

        Assert.True(result.IsFailure);
        Assert.Equal(new EngineError(ErrorKind.Unavailable, "Vulkan loader missing: could not load " + path), result.Error);
    }

    [Fact]
    public void Preload_LibraryWithoutEntryPoint_IsUnavailableNamingPath()
    {
        // A native library every runtime ships next to System.Private.CoreLib, which exports no Vulkan.
        string path = Path.Join(
            Path.GetDirectoryName(typeof(object).Assembly.Location),
            OperatingSystem.IsWindows() ? "System.IO.Compression.Native.dll"
                : OperatingSystem.IsMacOS() ? "libSystem.IO.Compression.Native.dylib" : "libSystem.IO.Compression.Native.so");
        Assert.True(File.Exists(path), path);

        Result<nint> result = default(ProcessLoaderHost).Preload(path);

        Assert.True(result.IsFailure);
        Assert.Equal(new EngineError(ErrorKind.Unavailable, "Vulkan loader missing: no vkGetInstanceProcAddr export in " + path), result.Error);
    }

    [Fact]
    public void Preload_RelativePath_Throws()
    {
        Assert.Throws<ArgumentException>("path", () => default(ProcessLoaderHost).Preload("vulkan-1.dll"));
    }

    [Fact]
    public void IsMacOS_MatchesOperatingSystem()
    {
        Assert.Equal(OperatingSystem.IsMacOS(), default(ProcessLoaderHost).IsMacOS);
    }

    [Fact]
    public void FileExists_ReportsTheFileSystem()
    {
        var host = default(ProcessLoaderHost);

        Assert.True(host.FileExists(typeof(object).Assembly.Location));
        Assert.False(host.FileExists(Path.Join(AppContext.BaseDirectory, "no-such-file")));
    }
}
