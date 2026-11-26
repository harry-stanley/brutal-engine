using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Logging.Sinks;
using BrutalEngine.Graphics.Vulkan.Loader;
using BrutalEngine.Graphics.Vulkan.Tests.Loader;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests;

public sealed class VulkanBackendTests
{
    private const nint SystemEntry = 0x1000;
    private const nint ExplicitEntry = 0x2000;
    private const nint BundledEntry = 0x3000;

    private static readonly string _app = Path.Join(Path.GetTempPath(), "brutal-app");
    private static readonly string _explicitLoader = Path.Join(Path.GetTempPath(), "loaders", "vulkan-1.dll");
    private static readonly string _otherLoader = Path.Join(Path.GetTempPath(), "loaders", "other", "vulkan-1.dll");
    private static readonly string _bundledLoader = Path.Join(_app, LoaderSetup.LoaderFileName);
    private static readonly string _bundledIcd = Path.Join(_app, LoaderSetup.IcdFileName);

    public static TheoryData<string> MisuseCases =>
    [
        "AppDirectory null",
        "AppDirectory empty",
        "AppDirectory relative",
        "AppDirectory drive-relative",
        "Logger null",
        "LoaderPath empty",
        "LoaderPath relative",
        "Reason null",
        "Reason empty",
        "Reason white space",
    ];

    public static TheoryData<string> LoaderFailureCases =>
    [
        "no loader",
        "explicit loader not loadable",
        "BRUTAL bound another loader",
        "setenv failed",
    ];

    public static TheoryData<string> LoaderChoiceCases =>
    [
        "system",
        "explicit",
        "bundled",
        "bundled, user's driver files",
    ];

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_SystemLoader_ReturnsBackend()
    {
        // Windows and Linux with no explicit loader: BRUTAL's own search binds the system loader.
        MemoryLogSink sink = new(16, 4096);
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });

        Result<VulkanBackend> result = VulkanBackend.Create(new VulkanBackendOptions { AppDirectory = AppContext.BaseDirectory, Logger = logger });

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal("Vulkan", result.Value.Name);
        Assert.Null(result.Value.LoaderPath);
        Assert.Equal([(LogLevel.Information, "Graphics.Vulkan", "Vulkan loader: BRUTAL's search (system)")], Entries(sink));
    }

    [Fact]
    [Trait("Category", "Gpu")]
    public void Create_Twice_Idempotent()
    {
        MemoryLogSink sink = new(16, 4096);
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        VulkanBackendOptions options = new() { AppDirectory = AppContext.BaseDirectory, Logger = logger };

        Result<VulkanBackend> first = VulkanBackend.Create(options);
        Result<VulkanBackend> second = VulkanBackend.Create(options);

        Assert.True(first.IsSuccess, first.ToString());
        Assert.True(second.IsSuccess, second.ToString());
        Assert.NotSame(first.Value, second.Value);
        Assert.Equal(first.Value.LoaderPath, second.Value.LoaderPath);
        Assert.Equal(2, Entries(sink).Count);
    }

    [Theory]
    [MemberData(nameof(MisuseCases))]
    public void Create_Misuse_Throws(string misuse)
    {
        MemoryLogSink sink = new(16, 4096);
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        VulkanBackendOptions valid = new() { AppDirectory = _app, Logger = logger };
        ValidationAllowance good = new(1, "known false positive");
        VulkanBackendOptions options = misuse switch
        {
            "AppDirectory null" => valid with { AppDirectory = null! },
            "AppDirectory empty" => valid with { AppDirectory = string.Empty },
            "AppDirectory relative" => valid with { AppDirectory = Path.Join("bin", "app") },
            "AppDirectory drive-relative" => valid with { AppDirectory = "C:app" },
            "Logger null" => valid with { Logger = null! },
            "LoaderPath empty" => valid with { LoaderPath = string.Empty },
            "LoaderPath relative" => valid with { LoaderPath = "vulkan-1.dll" },
            "Reason null" => valid with { ValidationAllowList = new[] { good, new ValidationAllowance(2, null!) } },
            "Reason empty" => valid with { ValidationAllowList = new[] { good, new ValidationAllowance(2, string.Empty) } },
            "Reason white space" => valid with { ValidationAllowList = new[] { good, new ValidationAllowance(2, " \t") } },
            _ => throw new ArgumentOutOfRangeException(nameof(misuse), misuse, null),
        };
        FakeLoaderHost host = SystemHost();

        // The parameter is the options record, so the backend's own check threw, not the loader setup's.
        ArgumentException exception = Assert.Throws<ArgumentException>("options", () => VulkanBackend.Create(options, host));

        Assert.Contains(misuse.StartsWith("Reason", StringComparison.Ordinal) ? "ValidationAllowList[1]" : misuse.Split(' ')[0], exception.Message, StringComparison.Ordinal);
        Assert.Empty(host.Effects);
        Assert.Empty(Entries(sink));
    }

    [Fact]
    public void Create_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>("options", () => VulkanBackend.Create(null!));
    }

    [Fact]
    public void Create_NullHost_Throws()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });

        Assert.Throws<ArgumentNullException>(
            "host", () => VulkanBackend.Create<FakeLoaderHost>(new VulkanBackendOptions { AppDirectory = _app, Logger = logger }, null!));
    }

    [Fact]
    public void Create_DisposedLogger_ThrowsBeforeTouchingTheLoader()
    {
        Logger logger = new(new LoggerOptions { Sinks = [] });
        logger.Dispose();
        FakeLoaderHost host = SystemHost();

        Assert.Throws<ObjectDisposedException>(() => VulkanBackend.Create(new VulkanBackendOptions { AppDirectory = _app, Logger = logger }, host));
        Assert.Empty(host.Effects);
    }

    [Theory]
    [MemberData(nameof(LoaderFailureCases))]
    public void Create_LoaderFailures_AreResults(string failure)
    {
        MemoryLogSink sink = new(16, 4096);
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        VulkanBackendOptions options = new() { AppDirectory = _app, Logger = logger };
        EngineError setenvFailure = new(ErrorKind.Native, "setenv(VK_DRIVER_FILES) failed: fake", 12);
        FakeLoaderHost host;
        EngineError expected;
        switch (failure)
        {
            case "no loader":
                host = SystemHost().WithoutSystemLoader();
                expected = FakeLoaderHost.BrutalMissing;
                break;
            case "explicit loader not loadable":
                host = SystemHost();
                options = options with { LoaderPath = _explicitLoader };
                expected = FakeLoaderHost.PreloadFailure(_explicitLoader);
                break;
            case "BRUTAL bound another loader":
                // An earlier touch of BRUTAL's Vulkan class bound the system loader for the process.
                host = SystemHost();
                host.Libraries[_explicitLoader] = ExplicitEntry;
                Assert.True(host.BrutalGetInstanceProcAddr().IsSuccess);
                options = options with { LoaderPath = _explicitLoader };
                expected = new EngineError(ErrorKind.Unavailable, "BRUTAL resolved a different Vulkan loader than " + _explicitLoader);
                break;
            case "setenv failed":
                host = new FakeLoaderHost { IsMacOS = true, SystemEntryPoint = SystemEntry }.WithSetenvFailure(setenvFailure);
                host.Files.Add(_bundledIcd);
                expected = setenvFailure;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(failure), failure, null);
        }

        Result<VulkanBackend> result = VulkanBackend.Create(options, host);

        Assert.True(result.IsFailure, result.ToString());
        Assert.Equal(expected, result.Error);
        Assert.Empty(Entries(sink));
    }

    [Theory]
    [MemberData(nameof(LoaderChoiceCases))]
    public void Create_LoaderChoice_ReturnsPathAndLogsDecision(string choice)
    {
        MemoryLogSink sink = new(16, 4096);
        using Logger logger = new(new LoggerOptions { Sinks = [sink] });
        VulkanBackendOptions options = new() { AppDirectory = _app, Logger = logger };
        FakeLoaderHost host = SystemHost();
        string? expectedPath;
        string expectedLine;
        switch (choice)
        {
            case "system":
                expectedPath = null;
                expectedLine = "Vulkan loader: BRUTAL's search (system)";
                break;
            case "explicit":
                host.Libraries[_explicitLoader] = ExplicitEntry;
                options = options with { LoaderPath = _explicitLoader };
                expectedPath = _explicitLoader;
                expectedLine = $"Vulkan loader: {_explicitLoader} (explicit)";
                break;
            case "bundled":
                host = BundledHost();
                expectedPath = _bundledLoader;
                expectedLine = $"Vulkan loader: {_bundledLoader} (bundled), driver files: {_bundledIcd}";
                break;
            case "bundled, user's driver files":
                host = BundledHost().WithEnvironment(LoaderSetup.DriverFilesVariable, "/opt/icd/user.json");
                expectedPath = _bundledLoader;
                expectedLine = $"Vulkan loader: {_bundledLoader} (bundled), driver files: /opt/icd/user.json";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(choice), choice, null);
        }

        Result<VulkanBackend> result = VulkanBackend.Create(options, host);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal("Vulkan", result.Value.Name);
        Assert.Equal(expectedPath, result.Value.LoaderPath);
        Assert.Equal([(LogLevel.Information, "Graphics.Vulkan", expectedLine)], Entries(sink));
    }

    [Fact]
    public void Create_SecondWithSameLoader_Succeeds()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        FakeLoaderHost host = SystemHost();
        host.Libraries[_explicitLoader] = ExplicitEntry;
        VulkanBackendOptions options = new() { AppDirectory = _app, Logger = logger, LoaderPath = _explicitLoader };

        Assert.True(VulkanBackend.Create(options, host).IsSuccess);
        Result<VulkanBackend> second = VulkanBackend.Create(options, host);

        Assert.True(second.IsSuccess, second.ToString());
        Assert.Equal(_explicitLoader, second.Value.LoaderPath);
    }

    [Fact]
    public void Create_SecondWithDifferentLoader_IsUnavailable()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        FakeLoaderHost host = SystemHost();
        host.Libraries[_explicitLoader] = ExplicitEntry;
        host.Libraries[_otherLoader] = ExplicitEntry + 1;
        VulkanBackendOptions options = new() { AppDirectory = _app, Logger = logger, LoaderPath = _explicitLoader };
        Assert.True(VulkanBackend.Create(options, host).IsSuccess);

        Result<VulkanBackend> second = VulkanBackend.Create(options with { LoaderPath = _otherLoader }, host);

        Assert.True(second.IsFailure, second.ToString());
        Assert.Equal(new EngineError(ErrorKind.Unavailable, "BRUTAL resolved a different Vulkan loader than " + _otherLoader), second.Error);
    }

    [Fact]
    public void Create_ValidationAllowList_IsCopied()
    {
        using Logger logger = new(new LoggerOptions { Sinks = [] });
        ValidationAllowance[] allowList = [new(7, "first"), new(-8, "second")];
        VulkanBackendOptions options = new() { AppDirectory = _app, Logger = logger, ValidationAllowList = allowList };

        VulkanBackend backend = VulkanBackend.Create(options, SystemHost()).Value;
        allowList[0] = new ValidationAllowance(9, "changed after creation");

        Assert.Equal([new(7, "first"), new(-8, "second")], backend.ValidationAllowList.ToArray());
    }

    [Fact]
    public void VulkanBackend_IsNotDisposable()
    {
        // The backend holds no native object: the loader belongs to the process (the note's "stateless backend").
        Assert.False(typeof(IDisposable).IsAssignableFrom(typeof(VulkanBackend)));
    }

    private static FakeLoaderHost SystemHost() => new() { SystemEntryPoint = SystemEntry };

    private static FakeLoaderHost BundledHost()
    {
        FakeLoaderHost host = new() { IsMacOS = true, SystemEntryPoint = SystemEntry };
        host.Files.Add(_bundledLoader);
        host.Files.Add(_bundledIcd);
        host.Libraries[_bundledLoader] = BundledEntry;
        return host;
    }

    private static List<(LogLevel Level, string Category, string Message)> Entries(MemoryLogSink sink)
    {
        var records = new MemoryLogRecord[sink.CapacityEntries];
        byte[] messages = new byte[sink.CapacityBytes];
        int count = sink.CopyTo(records, messages);
        List<(LogLevel, string, string)> entries = [];
        for (int i = 0; i < count; i++)
        {
            entries.Add((records[i].Level, records[i].Category, Encoding.UTF8.GetString(records[i].Message.Span)));
        }

        return entries;
    }
}
