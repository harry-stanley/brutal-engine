using System;
using System.Collections.Generic;
using System.Linq;
using BrutalEngine.Graphics.Vulkan.Adapters;
using Xunit;
using static BrutalEngine.Graphics.Vulkan.Tests.Adapters.Candidates;

namespace BrutalEngine.Graphics.Vulkan.Tests.Adapters;

public sealed class AdapterRulesTests
{
    // The reasons, written from the issue and the rules' documentation, never read from the code under test.
    private const string DynamicRendering = "dynamicRendering is missing";
    private const string Synchronization2 = "synchronization2 is missing";
    private const string Swapchain = "VK_KHR_swapchain is missing";
    private const string NoGraphics = "no queue family supports graphics";
    private const string NoGraphicsPresent = "no queue family supports graphics and presents to the window";
    private const string Anisotropy = "SamplerAnisotropy: samplerAnisotropy is missing";
    private const string TimestampBits = "TimestampQueries: the graphics queue has no timestamp bits";
    private const string TimestampPeriod = "TimestampQueries: the timestamp period is not positive";
    private const string BC = "TextureCompressionBC: textureCompressionBC is missing";
    private const string Astc = "TextureCompressionAstc: textureCompressionASTC_LDR is missing";

    private static readonly AdapterRules.RulesRequest _everything = new(AllFeatures, HasCompatibleSurface: true);

    // One failed rule per row: the candidate meets everything else the request needs, so exactly one reason is expected.
    private static readonly Dictionary<string, (AdapterCandidate Candidate, AdapterRules.RulesRequest Request, string Reason)> _missing = new()
    {
        ["version 1.2.999"] = (Suitable() with { ApiVersionMinor = 2, ApiVersionPatch = 999 }, _everything, "Vulkan 1.2.999 is below the required 1.3"),
        ["version 1.0.0"] = (Suitable() with { ApiVersionMinor = 0, ApiVersionPatch = 0 }, _everything, "Vulkan 1.0.0 is below the required 1.3"),
        ["version 0.9.0"] = (Suitable() with { ApiVersionMajor = 0, ApiVersionMinor = 9, ApiVersionPatch = 0 }, _everything, "Vulkan 0.9.0 is below the required 1.3"),
        ["version 0.3.0"] = (Suitable() with { ApiVersionMajor = 0, ApiVersionMinor = 3, ApiVersionPatch = 0 }, _everything, "Vulkan 0.3.0 is below the required 1.3"),
        ["dynamicRendering"] = (Suitable() with { DynamicRendering = false }, _everything, DynamicRendering),
        ["synchronization2"] = (Suitable() with { Synchronization2 = false }, _everything, Synchronization2),
        ["no extensions"] = (Suitable() with { DeviceExtensions = Array.Empty<string>() }, _everything, Swapchain),
        ["other extensions only"] = (Suitable() with { DeviceExtensions = new[] { "VK_KHR_maintenance5", "VK_EXT_memory_budget" } }, _everything, Swapchain),
        ["swapchain extension in another case"] = (Suitable() with { DeviceExtensions = new[] { "vk_khr_swapchain" } }, _everything, Swapchain),
        ["only an extension the swapchain name prefixes"] = (Suitable() with { DeviceExtensions = new[] { "VK_KHR_swapchain_maintenance1" } }, _everything, Swapchain),
        ["no queue families"] = (Suitable() with { QueueFamilies = Array.Empty<AdapterCandidate.QueueFamily>() }, new(AllFeatures, false), NoGraphics),
        ["no graphics family"] = (
            Suitable() with { QueueFamilies = new[] { Family(0, graphics: false, compute: true, present: true), Family(1, graphics: false, compute: false, present: true) } },
            new(AllFeatures, false), NoGraphics),
        ["no graphics family, with a surface"] = (
            Suitable() with { QueueFamilies = new[] { Family(0, graphics: false, compute: true, present: true) } }, _everything, NoGraphicsPresent),
        ["graphics family can't present"] = (
            Suitable() with { QueueFamilies = new[] { Family(0, graphics: true, compute: true, present: false) } }, _everything, NoGraphicsPresent),
        ["graphics and present on different families"] = (
            Suitable() with { QueueFamilies = new[] { Family(0, graphics: false, compute: true, present: true), Family(1, graphics: true, compute: true, present: false) } },
            _everything, NoGraphicsPresent),
        ["samplerAnisotropy"] = (Suitable() with { SamplerAnisotropy = false }, _everything, Anisotropy),
        ["timestamp bits on the only family"] = (
            Suitable() with { QueueFamilies = new[] { Family(0, graphics: true, compute: true, present: true, timestampBits: 0) } }, _everything, TimestampBits),
        ["timestamp bits on the chosen family, not an earlier passing one"] = (
            Suitable() with
            {
                QueueFamilies = new[]
                {
                    Family(0, graphics: true, compute: false, present: true, timestampBits: 64),
                    Family(1, graphics: true, compute: true, present: true, timestampBits: 0),
                },
            },
            _everything, TimestampBits),
        ["timestamp bits on the chosen family, not a later one"] = (
            Suitable() with
            {
                QueueFamilies = new[]
                {
                    Family(0, graphics: true, compute: true, present: true, timestampBits: 0),
                    Family(1, graphics: true, compute: true, present: true, timestampBits: 64),
                },
            },
            _everything, TimestampBits),
        ["timestamp bits on the presenting family, not one that can't present"] = (
            Suitable() with
            {
                QueueFamilies = new[]
                {
                    Family(0, graphics: true, compute: true, present: false, timestampBits: 64),
                    Family(1, graphics: true, compute: true, present: true, timestampBits: 0),
                },
            },
            _everything, TimestampBits),
        ["timestamp period zero"] = (Suitable() with { TimestampPeriod = 0f }, _everything, TimestampPeriod),
        ["timestamp period negative zero"] = (Suitable() with { TimestampPeriod = -0f }, _everything, TimestampPeriod),
        ["timestamp period negative"] = (Suitable() with { TimestampPeriod = -1f }, _everything, TimestampPeriod),
        ["timestamp period NaN"] = (Suitable() with { TimestampPeriod = float.NaN }, _everything, TimestampPeriod),
        ["timestamp period infinite"] = (Suitable() with { TimestampPeriod = float.PositiveInfinity }, _everything, TimestampPeriod),
        ["textureCompressionBC"] = (Suitable() with { TextureCompressionBC = false }, _everything, BC),
        ["textureCompressionASTC_LDR"] = (Suitable() with { TextureCompressionAstcLdr = false }, _everything, Astc),
    };

    // Candidates at or just inside each bound, and queue-family layouts that pass: no reason expected.
    private static readonly Dictionary<string, (AdapterCandidate Candidate, AdapterRules.RulesRequest Request)> _passing = new()
    {
        ["version 1.3.0"] = (Suitable() with { ApiVersionPatch = 0 }, _everything),
        ["version 1.4.0"] = (Suitable() with { ApiVersionMinor = 4, ApiVersionPatch = 0 }, _everything),
        ["version 2.0.0"] = (Suitable() with { ApiVersionMajor = 2, ApiVersionMinor = 0, ApiVersionPatch = 0 }, _everything),
        ["only the swapchain extension"] = (Suitable() with { DeviceExtensions = new[] { "VK_KHR_swapchain" } }, _everything),
        ["graphics without compute"] = (
            Suitable() with { QueueFamilies = new[] { Family(0, graphics: true, compute: false, present: true) } }, _everything),
        ["presenting graphics family after one that can't present"] = (
            Suitable() with
            {
                QueueFamilies = new[]
                {
                    Family(0, graphics: true, compute: true, present: false, timestampBits: 0),
                    Family(1, graphics: true, compute: false, present: true, timestampBits: 64),
                },
            },
            _everything),
        ["one timestamp bit"] = (
            Suitable() with { QueueFamilies = new[] { Family(0, graphics: true, compute: true, present: true, timestampBits: 1) } }, _everything),
        ["timestamp bits only on the chosen family"] = (
            Suitable() with
            {
                QueueFamilies = new[]
                {
                    Family(0, graphics: true, compute: false, present: true, timestampBits: 0),
                    Family(1, graphics: true, compute: true, present: true, timestampBits: 64),
                    Family(2, graphics: true, compute: true, present: true, timestampBits: 0),
                },
            },
            _everything),
        ["smallest positive timestamp period"] = (Suitable() with { TimestampPeriod = float.Epsilon }, _everything),
        ["largest finite timestamp period"] = (Suitable() with { TimestampPeriod = float.MaxValue }, _everything),
        ["timestamp period zero, timestamps not required"] = (
            Suitable() with { TimestampPeriod = 0f }, new(AllFeatures & ~GraphicsFeatures.TimestampQueries, true)),
    };

    public static TheoryData<string> MissingCases => [.. _missing.Keys];

    public static TheoryData<string> PassingCases => [.. _passing.Keys];

    public static TheoryData<GraphicsFeatures, bool> Requests =>
        new() { { GraphicsFeatures.None, false }, { GraphicsFeatures.None, true }, { AllFeatures, false }, { AllFeatures, true } };

    public static TheoryData<GraphicsFeatures> FeatureFlags =>
    [
        GraphicsFeatures.SamplerAnisotropy,
        GraphicsFeatures.TimestampQueries,
        GraphicsFeatures.TextureCompressionBC,
        GraphicsFeatures.TextureCompressionAstc,
    ];

    [Theory]
    [MemberData(nameof(Requests))]
    public void AppendRejections_SuitableCandidate_AddsNothing(GraphicsFeatures required, bool hasSurface)
    {
        List<string> reasons = ["earlier"];

        AdapterRules.AppendRejections(Suitable(), new AdapterRules.RulesRequest(required, hasSurface), reasons);

        Assert.Equal(["earlier"], reasons);
    }

    [Theory]
    [MemberData(nameof(PassingCases))]
    public void AppendRejections_AtEachBound_AddsNothing(string name)
    {
        (AdapterCandidate candidate, AdapterRules.RulesRequest request) = _passing[name];

        Assert.Empty(Judge(candidate, request));
    }

    [Theory]
    [MemberData(nameof(MissingCases))]
    public void AppendRejections_EachMissingRequirement_AddsItsReason(string name)
    {
        (AdapterCandidate candidate, AdapterRules.RulesRequest request, string reason) = _missing[name];

        Assert.Equal([reason], Judge(candidate, request));
    }

    [Fact]
    public void AppendRejections_EachMissingRequirement_CoversEveryReason()
    {
        // Guards the theory's data: every reason the rules document has a row (the version reason by its prefix).
        string[] reasons = [DynamicRendering, Synchronization2, Swapchain, NoGraphics, NoGraphicsPresent, Anisotropy, TimestampBits, TimestampPeriod, BC, Astc];

        Assert.All(reasons, r => Assert.Contains(_missing.Values, row => row.Reason == r));
        Assert.Contains(_missing.Values, row => row.Reason.StartsWith("Vulkan ", StringComparison.Ordinal));
    }

    [Fact]
    public void AppendRejections_SeveralMissing_ReasonsInRuleOrder()
    {
        // The default candidate fails every rule; with no queue family, the timestamp rule checks only the period.
        Assert.Equal(
            ["Vulkan 0.0.0 is below the required 1.3", DynamicRendering, Synchronization2, Swapchain, NoGraphicsPresent, Anisotropy, TimestampPeriod, BC, Astc],
            Judge(default, _everything));
    }

    [Fact]
    public void AppendRejections_SeveralMissingWithAFamily_ReasonsInRuleOrder()
    {
        AdapterCandidate candidate = Suitable() with
        {
            ApiVersionMinor = 2,
            ApiVersionPatch = 7,
            Synchronization2 = false,
            TextureCompressionBC = false,
            QueueFamilies = new[] { Family(4, graphics: true, compute: false, present: true, timestampBits: 0) },
        };

        Assert.Equal(["Vulkan 1.2.7 is below the required 1.3", Synchronization2, TimestampBits, BC], Judge(candidate, _everything));
    }

    [Fact]
    public void AppendRejections_NoSurface_DoesNotNeedPresent()
    {
        AdapterCandidate candidate = Suitable() with { QueueFamilies = new[] { Family(0, graphics: true, compute: true, present: false) } };

        Assert.Empty(Judge(candidate, new(AllFeatures, HasCompatibleSurface: false)));
    }

    [Fact]
    public void AppendRejections_WithSurface_NeedsPresent()
    {
        AdapterCandidate candidate = Suitable() with { QueueFamilies = new[] { Family(0, graphics: true, compute: true, present: false) } };

        Assert.Equal([NoGraphicsPresent], Judge(candidate, new(GraphicsFeatures.None, HasCompatibleSurface: true)));
    }

    [Fact]
    public void AppendRejections_NoSurface_StillNeedsSwapchainExtension()
    {
        AdapterCandidate candidate = Suitable() with { DeviceExtensions = new[] { "VK_EXT_memory_budget" } };

        Assert.Equal([Swapchain], Judge(candidate, new(GraphicsFeatures.None, HasCompatibleSurface: false)));
    }

    [Theory]
    [MemberData(nameof(FeatureFlags))]
    public void AppendRejections_RequiredFeatureMissing_Rejects(GraphicsFeatures flag)
    {
        Assert.Equal([ReasonFor(flag)], Judge(Lacking(flag), new(flag, HasCompatibleSurface: false)));
    }

    [Theory]
    [MemberData(nameof(FeatureFlags))]
    public void AppendRejections_FeatureMissingButNotRequired_AddsNothing(GraphicsFeatures flag)
    {
        Assert.Empty(Judge(Lacking(flag), new(AllFeatures & ~flag, HasCompatibleSurface: true)));
    }

    [Fact]
    public void FeatureFlags_CoverEveryGraphicsFeaturesMember()
    {
        GraphicsFeatures[] members = [.. Enum.GetValues<GraphicsFeatures>().Where(f => f != GraphicsFeatures.None)];

        Assert.Equal(members.Order(), FeatureFlags.Select(row => row.Data).Order());
    }

    [Theory]
    [InlineData(0, 1.0f, TimestampBits)]
    [InlineData(64, 0f, TimestampPeriod)]
    public void TimestampQueries_ZeroBitsOrZeroPeriod_Rejects(int bits, float period, string reason)
    {
        AdapterCandidate candidate = Suitable() with
        {
            TimestampPeriod = period,
            QueueFamilies = new[] { Family(0, graphics: true, compute: true, present: true, timestampBits: bits) },
        };

        Assert.Equal([reason], Judge(candidate, new(GraphicsFeatures.TimestampQueries, HasCompatibleSurface: true)));
    }

    [Fact]
    public void TimestampQueries_ZeroBitsAndZeroPeriod_OneReason()
    {
        AdapterCandidate candidate = Suitable() with
        {
            TimestampPeriod = 0f,
            QueueFamilies = new[] { Family(0, graphics: true, compute: true, present: true, timestampBits: 0) },
        };

        Assert.Equal([TimestampBits], Judge(candidate, new(GraphicsFeatures.TimestampQueries, HasCompatibleSurface: true)));
    }

    [Fact]
    public void TimestampQueries_NoQueueFamily_OnlyTheFamilyReason()
    {
        AdapterCandidate candidate = Suitable() with { QueueFamilies = Array.Empty<AdapterCandidate.QueueFamily>() };

        Assert.Equal([NoGraphics], Judge(candidate, new(GraphicsFeatures.TimestampQueries, HasCompatibleSurface: false)));
    }

    [Fact]
    public void TimestampQueries_NoQueueFamily_StillChecksThePeriod()
    {
        AdapterCandidate candidate = Suitable() with { TimestampPeriod = 0f, QueueFamilies = Array.Empty<AdapterCandidate.QueueFamily>() };

        Assert.Equal([NoGraphics, TimestampPeriod], Judge(candidate, new(GraphicsFeatures.TimestampQueries, HasCompatibleSurface: false)));
    }

    [Fact]
    public void AppendRejections_NullList_Throws()
    {
        ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => AdapterRules.AppendRejections(Suitable(), _everything, null!));

        Assert.Equal("reasons", ex.ParamName);
    }

    private static List<string> Judge(in AdapterCandidate candidate, in AdapterRules.RulesRequest request)
    {
        List<string> reasons = [];
        AdapterRules.AppendRejections(candidate, request, reasons);
        return reasons;
    }

    private static AdapterCandidate Lacking(GraphicsFeatures flag) => flag switch
    {
        GraphicsFeatures.SamplerAnisotropy => Suitable() with { SamplerAnisotropy = false },
        GraphicsFeatures.TimestampQueries => Suitable() with
        {
            QueueFamilies = new[] { Family(0, graphics: true, compute: true, present: true, timestampBits: 0) },
        },
        GraphicsFeatures.TextureCompressionBC => Suitable() with { TextureCompressionBC = false },
        GraphicsFeatures.TextureCompressionAstc => Suitable() with { TextureCompressionAstcLdr = false },
        _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "Not a single GraphicsFeatures flag."),
    };

    private static string ReasonFor(GraphicsFeatures flag) => flag switch
    {
        GraphicsFeatures.SamplerAnisotropy => Anisotropy,
        GraphicsFeatures.TimestampQueries => TimestampBits,
        GraphicsFeatures.TextureCompressionBC => BC,
        GraphicsFeatures.TextureCompressionAstc => Astc,
        _ => throw new ArgumentOutOfRangeException(nameof(flag), flag, "Not a single GraphicsFeatures flag."),
    };
}
