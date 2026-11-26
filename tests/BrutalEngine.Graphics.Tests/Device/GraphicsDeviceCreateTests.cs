using System;
using System.Collections.Generic;
using System.Linq;
using BrutalEngine.Core;
using BrutalEngine.Core.Logging;
using BrutalEngine.Graphics.Tests.Backend;
using BrutalEngine.TestUtilities;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Device;

/// <summary>
/// Device creation, adapter enumeration and format support against the fake backend (docs/design/graphics.md
/// section 2).
/// </summary>
public sealed class GraphicsDeviceCreateTests : IDisposable
{
    private readonly DeviceFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Create_WithFake_Succeeds()
    {
        var window = new FakeWindow { PixelSize = new(640, 480), VulkanSurface = new FakeVulkanSurfaceSource([], (nint)1) };
        GraphicsDeviceOptions options = _fixture.Options() with
        {
            CompatibleWindow = window,
            Validation = true,
            FramesInFlight = 3,
            RequiredFeatures = GraphicsFeatures.SamplerAnisotropy,
            OptionalFeatures = GraphicsFeatures.TimestampQueries,
            GpuTimeout = TimeSpan.FromSeconds(3),
            ApplicationName = "CreateTest",
        };
        _fixture.Script.Capabilities = FakeBackendScript.DefaultCapabilities with { Features = GraphicsFeatures.SamplerAnisotropy };

        GraphicsDevice device = _fixture.Create(options);

        FakeBackendDevice backend = _fixture.BackendDevice;
        Assert.Same(_fixture.Script.Adapters[0], device.Adapter);
        Assert.Same(backend.Capabilities, device.Capabilities);
        Assert.Same(_fixture.Logger, backend.Request.Logger);
        Assert.Same(_fixture.Allocator, backend.Request.Allocator);
        Assert.True(backend.Request.Validation);
        Assert.Equal(3, backend.Request.FramesInFlight);
        Assert.Equal(GraphicsFeatures.SamplerAnisotropy, backend.Request.RequiredFeatures);
        Assert.Equal(GraphicsFeatures.TimestampQueries, backend.Request.OptionalFeatures);
        Assert.Equal(TimeSpan.FromSeconds(3), backend.Request.GpuTimeout);
        Assert.Equal("CreateTest", backend.Request.ApplicationName);
        Assert.NotNull(backend.Request.CompatibleSurface);
        Assert.Equal((640, 480), (backend.Request.CompatibleSurface.PixelWidth, backend.Request.CompatibleSurface.PixelHeight));
        Assert.False(backend.IsDisposed);
    }

    [Fact]
    public void Create_WithoutWindow_IsOffscreen()
    {
        _ = _fixture.Create();

        Assert.Null(_fixture.BackendDevice.Request.CompatibleSurface);
    }

    public static TheoryData<string, int> Selections() => new()
    {
        { "Auto", 1 },
        { "Index2", 2 },
        { "IndexOfFirstSuitable", 1 },
        { "NameLowerCase", 3 },
        { "NameSubstringOfSeveral", 1 },
        { "TypeIntegrated", 2 },
        { "TypeOfSeveral", 1 },
    };

    [Theory]
    [MemberData(nameof(Selections))]
    public void Create_Selection_PicksBestSuitableMatch(string selection, int expected)
    {
        // 0 is rejected; 1 and 3 are discrete, 2 integrated; names "Alpha", "Shared Beta", "Shared Gamma", "Delta".
        SetAdapters(
            Adapter(0, "Alpha", AdapterType.Discrete, "no Vulkan 1.3"),
            Adapter(1, "Shared Beta", AdapterType.Discrete),
            Adapter(2, "Shared Gamma", AdapterType.Integrated),
            Adapter(3, "Delta", AdapterType.Discrete));
        AdapterSelection chosen = selection switch
        {
            "Auto" => AdapterSelection.Auto,
            "Index2" => AdapterSelection.ByIndex(2),
            "IndexOfFirstSuitable" => AdapterSelection.ByIndex(1),
            "NameLowerCase" => AdapterSelection.ByName("delta"),
            "NameSubstringOfSeveral" => AdapterSelection.ByName("SHARED"),
            "TypeIntegrated" => AdapterSelection.ByType(AdapterType.Integrated),
            "TypeOfSeveral" => AdapterSelection.ByType(AdapterType.Discrete),
            _ => throw new ArgumentOutOfRangeException(nameof(selection), selection, null),
        };

        GraphicsDevice device = _fixture.Create(_fixture.Options() with { Adapter = chosen });

        Assert.Equal(expected, device.Adapter.Index);
    }

    public static TheoryData<string> FailureRules() =>
    [
        "NoAdapters", "AllRejected", "IndexOverrideUnmatched", "IndexOverrideRejected", "NameOverrideUnmatched",
        "NameOverrideRejected", "TypeOverrideUnmatched", "MissingRequiredFeature", "SurfacelessWindow",
        "EnumerationFails", "CreateDeviceFails",
    ];

    [Theory]
    [MemberData(nameof(FailureRules))]
    public void Create_Failures_ReturnKinds(string rule)
    {
        GraphicsDeviceOptions options = _fixture.Options();
        var backendError = new EngineError(ErrorKind.Unavailable, "Vulkan loader missing", -9);
        (ErrorKind Kind, string[] Contains) expected;
        bool deviceCreated = false;
        switch (rule)
        {
            case "NoAdapters":
                _fixture.Script.Adapters.Clear();
                expected = (ErrorKind.Unsupported, ["no suitable adapter", "found no adapters"]);
                break;
            case "AllRejected":
                SetAdapters(Adapter(0, "Alpha", AdapterType.Discrete, "no Vulkan 1.3", "no swapchain"), Adapter(1, "Beta", AdapterType.Cpu, "no graphics queue"));
                expected = (ErrorKind.Unsupported, ["no suitable adapter", "Adapter 0 'Alpha'", "no Vulkan 1.3; no swapchain", "Adapter 1 'Beta'", "no graphics queue"]);
                break;
            case "IndexOverrideUnmatched":
                options = options with { Adapter = AdapterSelection.ByIndex(5) };
                expected = (ErrorKind.Unsupported, ["override 'index:5' matches no suitable adapter", "Adapter 0 'Fake GPU 0': suitable"]);
                break;
            case "IndexOverrideRejected":
                // The override names a rejected adapter while another is suitable: never a fallback to it.
                SetAdapters(Adapter(0, "Alpha", AdapterType.Discrete, "no swapchain"), Adapter(1, "Beta", AdapterType.Discrete));
                options = options with { Adapter = AdapterSelection.ByIndex(0) };
                expected = (ErrorKind.Unsupported, ["override 'index:0' matches no suitable adapter", "no swapchain", "Adapter 1 'Beta': suitable"]);
                break;
            case "NameOverrideUnmatched":
                options = options with { Adapter = AdapterSelection.ByName("Radeon") };
                expected = (ErrorKind.Unsupported, ["override 'name:Radeon' matches no suitable adapter"]);
                break;
            case "NameOverrideRejected":
                SetAdapters(Adapter(0, "Radeon", AdapterType.Discrete, "no Vulkan 1.3"), Adapter(1, "Beta", AdapterType.Discrete));
                options = options with { Adapter = AdapterSelection.ByName("radeon") };
                expected = (ErrorKind.Unsupported, ["override 'name:radeon' matches no suitable adapter", "no Vulkan 1.3"]);
                break;
            case "TypeOverrideUnmatched":
                options = options with { Adapter = AdapterSelection.ByType(AdapterType.Cpu) };
                expected = (ErrorKind.Unsupported, ["override 'type:cpu' matches no suitable adapter"]);
                break;
            case "MissingRequiredFeature":
                options = options with { RequiredFeatures = GraphicsFeatures.SamplerAnisotropy | GraphicsFeatures.TimestampQueries };
                _fixture.Script.Capabilities = FakeBackendScript.DefaultCapabilities with { Features = GraphicsFeatures.TimestampQueries };
                expected = (ErrorKind.Unsupported, ["adapter 0 'Fake GPU 0' lacks the required features SamplerAnisotropy."]);
                deviceCreated = true;
                break;
            case "SurfacelessWindow":
                options = options with { CompatibleWindow = new FakeWindow { PixelSize = new(8, 8) } };
                expected = (ErrorKind.Unsupported, ["window was created with WindowSurface.None"]);
                break;
            case "EnumerationFails":
                _fixture.Script.FailNext(FakeBackendOperation.EnumerateAdapters, backendError);
                expected = (backendError.Kind, [backendError.Message]);
                break;
            case "CreateDeviceFails":
                backendError = new EngineError(ErrorKind.OutOfMemory, "out of device memory", -2);
                _fixture.Script.FailNext(FakeBackendOperation.CreateDevice, backendError);
                expected = (backendError.Kind, [backendError.Message]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(rule), rule, null);
        }

        Result<GraphicsDevice> result = GraphicsDevice.Create(_fixture.Backend, options);

        Assert.True(result.TryGetError(out EngineError error), $"{rule}: Create succeeded.");
        Assert.Equal(expected.Kind, error.Kind);
        foreach (string part in expected.Contains)
        {
            Assert.Contains(part, error.Message, StringComparison.Ordinal);
        }

        if (rule is "EnumerationFails" or "CreateDeviceFails")
        {
            Assert.Equal(backendError, error);
        }

        // Nothing is left alive: either no backend device was made, or it was disposed before the failure returned.
        if (deviceCreated)
        {
            Assert.True(_fixture.BackendDevice.IsDisposed);
        }
        else
        {
            Assert.Null(_fixture.Backend.LastDevice);
        }
    }

    [Fact]
    public void Create_DoesNotDisposeBackend()
    {
        var backend = new CountingBackend(_fixture.Backend);

        GraphicsDevice device = GraphicsDevice.Create(backend, _fixture.Options()).Value;
        device.Dispose();
        Result<GraphicsDevice> unmatched = GraphicsDevice.Create(backend, _fixture.Options() with { Adapter = AdapterSelection.ByIndex(9) });
        _fixture.Script.FailNext(FakeBackendOperation.CreateDevice, new EngineError(ErrorKind.OutOfMemory, "full"));
        Result<GraphicsDevice> failed = GraphicsDevice.Create(backend, _fixture.Options());
        _fixture.Script.Capabilities = FakeBackendScript.DefaultCapabilities;
        Result<GraphicsDevice> missingFeature = GraphicsDevice.Create(
            backend, _fixture.Options() with { RequiredFeatures = GraphicsFeatures.SamplerAnisotropy });

        Assert.True(unmatched.IsFailure && failed.IsFailure && missingFeature.IsFailure);
        Assert.Equal(0, backend.DisposeCalls);
        Assert.Equal(3, backend.CreateCalls);
        // Still usable after all of that.
        GraphicsDevice again = GraphicsDevice.Create(backend, _fixture.Options()).Value;
        again.Dispose();
        Assert.Equal(0, backend.DisposeCalls);
    }

    public static TheoryData<string> InvalidArguments() => ["NullBackend", "NullOptions", "FramesInFlightZero", "FeaturesOverlap", "NullLogger"];

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void Create_InvalidArguments_ThrowBeforeTheBackend(string rule)
    {
        var backend = new CountingBackend(_fixture.Backend);

        ArgumentException thrown = Throwing(rule, options => GraphicsDevice.Create(rule == "NullBackend" ? null! : backend, options));

        AssertInvalidArgument(rule, thrown);
        Assert.Equal((0, 0), (backend.EnumerateCalls, backend.CreateCalls));
    }

    [Fact]
    public void Create_RejectedAdapters_AreLoggedWithReasons()
    {
        SetAdapters(
            Adapter(0, "Alpha", AdapterType.Discrete, "no Vulkan 1.3", "no swapchain"),
            Adapter(1, "Beta", AdapterType.Discrete),
            Adapter(2, "Gamma", AdapterType.Cpu, "no graphics queue"));

        _ = _fixture.Create();

        Assert.Equal(
            [
                (LogLevel.Information, "Graphics", "Adapter 0 'Alpha' rejected: no Vulkan 1.3; no swapchain."),
                (LogLevel.Information, "Graphics", "Adapter 2 'Gamma' rejected: no graphics queue."),
            ],
            _fixture.Entries());
    }

    [Fact]
    public void Create_NoSuitableAdapter_LogsEveryRejection()
    {
        SetAdapters(Adapter(0, "Alpha", AdapterType.Discrete, "no Vulkan 1.3"), Adapter(1, "Beta", AdapterType.Integrated, "no swapchain"));

        Assert.True(GraphicsDevice.Create(_fixture.Backend, _fixture.Options()).IsFailure);

        Assert.Equal(
            ["Adapter 0 'Alpha' rejected: no Vulkan 1.3.", "Adapter 1 'Beta' rejected: no swapchain."],
            _fixture.Entries().Select(entry => entry.Message));
    }

    [Fact]
    public void EnumerateAdapters_ListsRejectionReasons()
    {
        SetAdapters(Adapter(0, "Alpha", AdapterType.Discrete, "no Vulkan 1.3", "no swapchain"), Adapter(1, "Beta", AdapterType.Integrated));

        IReadOnlyList<GraphicsAdapterInfo> adapters = GraphicsDevice.EnumerateAdapters(_fixture.Backend, _fixture.Options()).Value;

        Assert.Equal(2, adapters.Count);
        Assert.Equal(["no Vulkan 1.3", "no swapchain"], adapters[0].RejectionReasons);
        Assert.Empty(adapters[1].RejectionReasons);
        Assert.Equal(["Alpha", "Beta"], adapters.Select(adapter => adapter.Name));
        Assert.Null(_fixture.Backend.LastDevice);
    }

    [Fact]
    public void EnumerateAdapters_IgnoresTheAdapterOverride()
    {
        Result<IReadOnlyList<GraphicsAdapterInfo>> result = GraphicsDevice.EnumerateAdapters(
            _fixture.Backend, _fixture.Options() with { Adapter = AdapterSelection.ByIndex(7) });

        Assert.Single(result.Value);
    }

    [Fact]
    public void EnumerateAdapters_NoBackendRuntime_FailsLikeCreate()
    {
        var error = new EngineError(ErrorKind.Unavailable, "Vulkan loader missing", -9);
        _fixture.Script.FailNext(FakeBackendOperation.EnumerateAdapters, error);

        Result<IReadOnlyList<GraphicsAdapterInfo>> result = GraphicsDevice.EnumerateAdapters(_fixture.Backend, _fixture.Options());

        Assert.True(result.TryGetError(out EngineError actual));
        Assert.Equal(error, actual);
    }

    [Fact]
    public void EnumerateAdapters_SurfacelessWindow_IsUnsupported()
    {
        var backend = new CountingBackend(_fixture.Backend);

        Result<IReadOnlyList<GraphicsAdapterInfo>> result = GraphicsDevice.EnumerateAdapters(
            backend, _fixture.Options() with { CompatibleWindow = new FakeWindow() });

        Assert.True(result.TryGetError(out EngineError error));
        Assert.Equal(ErrorKind.Unsupported, error.Kind);
        Assert.Contains("window was created with WindowSurface.None", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, backend.EnumerateCalls);
    }

    [Fact]
    public void EnumerateAdapters_WithWindow_PassesItsSurface()
    {
        var window = new FakeWindow { PixelSize = new(32, 16), VulkanSurface = new FakeVulkanSurfaceSource([], (nint)1) };
        var backend = new CountingBackend(_fixture.Backend);

        Assert.True(GraphicsDevice.EnumerateAdapters(backend, _fixture.Options() with { CompatibleWindow = window }).IsSuccess);

        Assert.NotNull(backend.LastRequest?.CompatibleSurface);
        Assert.Equal(32, backend.LastRequest.CompatibleSurface.PixelWidth);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void EnumerateAdapters_InvalidArguments_ThrowBeforeTheBackend(string rule)
    {
        var backend = new CountingBackend(_fixture.Backend);

        ArgumentException thrown = Throwing(rule, options => GraphicsDevice.EnumerateAdapters(rule == "NullBackend" ? null! : backend, options));

        AssertInvalidArgument(rule, thrown);
        Assert.Equal(0, backend.EnumerateCalls);
    }

    public static TheoryData<TextureFormat> DefinedFormats() =>
        [.. Enum.GetValues<TextureFormat>().Where(format => format != TextureFormat.Undefined)];

    [Theory]
    [MemberData(nameof(DefinedFormats))]
    public void GetFormatSupport_ReturnsBackendSupport(TextureFormat format)
    {
        // A distinct answer per format, so a lookup of the wrong format or a constant answer fails.
        _fixture.Script.FormatSupport = asked => (FormatSupport)((int)asked * 3);
        GraphicsDevice device = _fixture.Create();

        Assert.Equal((FormatSupport)((int)format * 3), device.GetFormatSupport(format));
        Assert.Equal(1, _fixture.BackendDevice.Calls.Count(call => call.Operation == FakeBackendOperation.GetFormatSupport));
    }

    public static TheoryData<int> InvalidFormats() => [0, -1, 17, int.MinValue, int.MaxValue];

    [Theory]
    [MemberData(nameof(InvalidFormats))]
    public void GetFormatSupport_InvalidFormat_ThrowsBeforeTheBackend(int format)
    {
        GraphicsDevice device = _fixture.Create();

        ArgumentOutOfRangeException thrown = Assert.Throws<ArgumentOutOfRangeException>(() => device.GetFormatSupport((TextureFormat)format));

        Assert.Equal("format", thrown.ParamName);
        Assert.DoesNotContain(_fixture.BackendDevice.Calls, call => call.Operation == FakeBackendOperation.GetFormatSupport);
    }

    private static GraphicsAdapterInfo Adapter(int index, string name, AdapterType type, params string[] reasons) =>
        FakeBackendScript.CreateAdapter(index, reasons) with { Name = name, Type = type };

    private void SetAdapters(params GraphicsAdapterInfo[] adapters)
    {
        _fixture.Script.Adapters.Clear();
        _fixture.Script.Adapters.AddRange(adapters);
    }

    private ArgumentException Throwing(string rule, Action<GraphicsDeviceOptions> call)
    {
        GraphicsDeviceOptions options = rule switch
        {
            "NullOptions" => null!,
            "FramesInFlightZero" => _fixture.Options() with { FramesInFlight = 0 },
            "FeaturesOverlap" => _fixture.Options() with
            {
                RequiredFeatures = GraphicsFeatures.SamplerAnisotropy,
                OptionalFeatures = GraphicsFeatures.SamplerAnisotropy,
            },
            "NullLogger" => _fixture.Options() with { Logger = null! },
            _ => _fixture.Options(),
        };
        return Assert.ThrowsAny<ArgumentException>(() => call(options));
    }

    private static void AssertInvalidArgument(string rule, ArgumentException thrown)
    {
        switch (rule)
        {
            case "NullBackend":
                Assert.Equal("backend", Assert.IsType<ArgumentNullException>(thrown).ParamName);
                break;
            case "NullOptions":
                Assert.Equal("options", Assert.IsType<ArgumentNullException>(thrown).ParamName);
                break;
            case "FramesInFlightZero":
                Assert.Contains("FramesInFlight", Assert.IsType<ArgumentOutOfRangeException>(thrown).Message, StringComparison.Ordinal);
                break;
            default:
                Assert.IsType<ArgumentException>(thrown);
                break;
        }
    }
}
