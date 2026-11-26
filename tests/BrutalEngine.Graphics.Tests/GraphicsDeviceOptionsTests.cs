using System;
using System.Globalization;
using BrutalEngine.Core.Logging;
using BrutalEngine.Core.Memory;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>The device options' defaults and validation rules (docs/design/graphics.md section 2).</summary>
public sealed class GraphicsDeviceOptionsTests : IDisposable
{
    // Every member, written out from docs/design/graphics.md section 2 (not computed from the enum under test).
    private const GraphicsFeatures AllFeatures = GraphicsFeatures.SamplerAnisotropy | GraphicsFeatures.TimestampQueries
        | GraphicsFeatures.TextureCompressionBC | GraphicsFeatures.TextureCompressionAstc;

    private readonly Logger _logger = new(new LoggerOptions { Sinks = [] });

    public void Dispose() => _logger.Dispose();

    [Fact]
    public void Defaults_MatchDesign()
    {
        var options = new GraphicsDeviceOptions { Logger = _logger };

        Assert.Same(_logger, options.Logger);
        Assert.IsType<HeapAllocator>(options.Allocator);
        Assert.Null(options.CompatibleWindow);
        Assert.Equal(AdapterSelection.Auto, options.Adapter);
        Assert.False(options.Validation);
        Assert.Equal(2, options.FramesInFlight);
        Assert.Equal(GraphicsFeatures.None, options.RequiredFeatures);
        Assert.Equal(GraphicsFeatures.None, options.OptionalFeatures);
        Assert.Equal(8L * 1024 * 1024, options.UploadRingBytesPerFrame);
        Assert.Equal(TimeSpan.FromSeconds(10), options.GpuTimeout);
        Assert.Equal("BrutalEngine", options.ApplicationName);
    }

    [Fact]
    public void Defaults_EachOptionsObject_GetsItsOwnAllocator()
    {
        var a = new GraphicsDeviceOptions { Logger = _logger };
        var b = new GraphicsDeviceOptions { Logger = _logger };

        Assert.NotSame(a.Allocator, b.Allocator);
    }

    public static TheoryData<string> InvalidRules =>
    [
        "LoggerNull", "AllocatorNull",
        "FramesInFlightZero", "FramesInFlightNegative", "FramesInFlightFour", "FramesInFlightMax",
        "RequiredFeaturesUndefinedFlag", "RequiredFeaturesNegative", "OptionalFeaturesUndefinedFlag", "OptionalFeaturesNegative",
        "FeaturesOverlapOne", "FeaturesOverlapPartly",
        "UploadRingBelowMinimum", "UploadRingZero", "UploadRingNegative", "UploadRingNotMultipleOf256", "UploadRingOddAboveMinimum",
        "GpuTimeoutZero", "GpuTimeoutNegative",
        "ApplicationNameNull", "ApplicationNameEmpty",
    ];

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public void Validate_Invalid_Throws(string rule)
    {
        (GraphicsDeviceOptions options, Type expected, string option) = Invalid(rule);

        Exception exception = Assert.Throws(expected, options.Validate);

        Assert.Equal("options", ((ArgumentException)exception).ParamName);
        Assert.StartsWith($"GraphicsDeviceOptions.{option} ", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> ValidRules =>
    [
        "Defaults", "FramesInFlightOne", "FramesInFlightThree", "FeaturesDisjoint", "FeaturesAllRequired", "FeaturesAllOptional",
        "UploadRingMinimum",
        "UploadRingLarge", "GpuTimeoutOneTick", "ApplicationNameOneCharacter",
    ];

    [Theory]
    [MemberData(nameof(ValidRules))]
    public void Validate_Valid_DoesNotThrow(string rule) => Valid(rule).Validate();

    [Fact]
    public void ToString_UnderAnotherCulture_ListsEveryPropertyInvariantly()
    {
        var options = new GraphicsDeviceOptions
        {
            Logger = _logger,
            Adapter = AdapterSelection.ByIndex(1),
            Validation = true,
            FramesInFlight = -1,
            RequiredFeatures = GraphicsFeatures.SamplerAnisotropy,
            OptionalFeatures = GraphicsFeatures.TimestampQueries | GraphicsFeatures.TextureCompressionBC,
            UploadRingBytesPerFrame = -256,
            GpuTimeout = TimeSpan.FromMilliseconds(-1500),
            ApplicationName = "Sample",
        };

        CultureInfo original = CultureInfo.CurrentCulture;
        // Swedish writes the minus sign as U+2212, so a culture-sensitive format of the negative values shows.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
        try
        {
            Assert.NotEqual("-1", (-1).ToString(CultureInfo.CurrentCulture));
            Assert.Equal(
                "GraphicsDeviceOptions { Logger = BrutalEngine.Core.Logging.Logger, Allocator = BrutalEngine.Core.Memory.HeapAllocator, "
                + "CompatibleWindow = , Adapter = index:1, Validation = True, FramesInFlight = -1, RequiredFeatures = SamplerAnisotropy, "
                + "OptionalFeatures = TimestampQueries, TextureCompressionBC, UploadRingBytesPerFrame = -256, GpuTimeout = -00:00:01.5000000, "
                + "ApplicationName = Sample }",
                options.ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private (GraphicsDeviceOptions Options, Type Expected, string Option) Invalid(string rule)
    {
        var valid = new GraphicsDeviceOptions { Logger = _logger };
        Type argument = typeof(ArgumentException);
        Type range = typeof(ArgumentOutOfRangeException);
        return rule switch
        {
            "LoggerNull" => (valid with { Logger = null! }, argument, "Logger"),
            "AllocatorNull" => (valid with { Allocator = null! }, argument, "Allocator"),
            "FramesInFlightZero" => (valid with { FramesInFlight = 0 }, range, "FramesInFlight"),
            "FramesInFlightNegative" => (valid with { FramesInFlight = -1 }, range, "FramesInFlight"),
            "FramesInFlightFour" => (valid with { FramesInFlight = 4 }, range, "FramesInFlight"),
            "FramesInFlightMax" => (valid with { FramesInFlight = int.MaxValue }, range, "FramesInFlight"),
            "RequiredFeaturesUndefinedFlag" => (valid with { RequiredFeatures = (GraphicsFeatures)(1L << 4) }, range, "RequiredFeatures"),
            "RequiredFeaturesNegative" => (valid with { RequiredFeatures = (GraphicsFeatures)long.MinValue }, range, "RequiredFeatures"),
            "OptionalFeaturesUndefinedFlag" => (
                valid with { OptionalFeatures = GraphicsFeatures.SamplerAnisotropy | (GraphicsFeatures)(1L << 40) }, range, "OptionalFeatures"),
            "OptionalFeaturesNegative" => (valid with { OptionalFeatures = (GraphicsFeatures)(-1L) }, range, "OptionalFeatures"),
            "FeaturesOverlapOne" => (
                valid with { RequiredFeatures = GraphicsFeatures.SamplerAnisotropy, OptionalFeatures = GraphicsFeatures.SamplerAnisotropy },
                argument, "RequiredFeatures"),
            "FeaturesOverlapPartly" => (
                valid with
                {
                    RequiredFeatures = GraphicsFeatures.SamplerAnisotropy | GraphicsFeatures.TextureCompressionAstc,
                    OptionalFeatures = GraphicsFeatures.TimestampQueries | GraphicsFeatures.TextureCompressionAstc,
                },
                argument, "RequiredFeatures"),
            "UploadRingBelowMinimum" => (valid with { UploadRingBytesPerFrame = (64 * 1024) - 256 }, range, "UploadRingBytesPerFrame"),
            "UploadRingZero" => (valid with { UploadRingBytesPerFrame = 0 }, range, "UploadRingBytesPerFrame"),
            "UploadRingNegative" => (valid with { UploadRingBytesPerFrame = long.MinValue }, range, "UploadRingBytesPerFrame"),
            "UploadRingNotMultipleOf256" => (valid with { UploadRingBytesPerFrame = (64 * 1024) + 128 }, argument, "UploadRingBytesPerFrame"),
            "UploadRingOddAboveMinimum" => (valid with { UploadRingBytesPerFrame = (8L << 20) + 1 }, argument, "UploadRingBytesPerFrame"),
            "GpuTimeoutZero" => (valid with { GpuTimeout = TimeSpan.Zero }, range, "GpuTimeout"),
            "GpuTimeoutNegative" => (valid with { GpuTimeout = TimeSpan.FromTicks(-1) }, range, "GpuTimeout"),
            "ApplicationNameNull" => (valid with { ApplicationName = null! }, argument, "ApplicationName"),
            "ApplicationNameEmpty" => (valid with { ApplicationName = "" }, argument, "ApplicationName"),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };
    }

    private GraphicsDeviceOptions Valid(string rule)
    {
        var valid = new GraphicsDeviceOptions { Logger = _logger };
        return rule switch
        {
            "Defaults" => valid,
            "FramesInFlightOne" => valid with { FramesInFlight = 1 },
            "FramesInFlightThree" => valid with { FramesInFlight = 3 },
            "FeaturesDisjoint" => valid with
            {
                RequiredFeatures = GraphicsFeatures.SamplerAnisotropy | GraphicsFeatures.TextureCompressionBC,
                OptionalFeatures = GraphicsFeatures.TimestampQueries | GraphicsFeatures.TextureCompressionAstc,
            },
            "FeaturesAllRequired" => valid with { RequiredFeatures = AllFeatures },
            "FeaturesAllOptional" => valid with { OptionalFeatures = AllFeatures },
            "UploadRingMinimum" => valid with { UploadRingBytesPerFrame = 64 * 1024 },
            "UploadRingLarge" => valid with { UploadRingBytesPerFrame = 1L << 40 },
            "GpuTimeoutOneTick" => valid with { GpuTimeout = TimeSpan.FromTicks(1) },
            "ApplicationNameOneCharacter" => valid with { ApplicationName = "x" },
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };
    }
}
