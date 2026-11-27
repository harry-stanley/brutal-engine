using System;
using System.Runtime.CompilerServices;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Adapters;
using BrutalEngine.Graphics.Vulkan.Devices;
using BrutalEngine.Graphics.Vulkan.Tests.Adapters;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Devices;

public sealed unsafe class FeatureChainTests
{
    private const GraphicsFeatures SA = GraphicsFeatures.SamplerAnisotropy;
    private const GraphicsFeatures TS = GraphicsFeatures.TimestampQueries;
    private const GraphicsFeatures BC = GraphicsFeatures.TextureCompressionBC;
    private const GraphicsFeatures Astc = GraphicsFeatures.TextureCompressionAstc;
    private const GraphicsFeatures All = Candidates.AllFeatures;
    private const GraphicsFeatures Undefined = (GraphicsFeatures)(1L << 10);

    // VkStructureType values from the Vulkan specification: PHYSICAL_DEVICE_FEATURES_2, PHYSICAL_DEVICE_VULKAN_1_3_FEATURES,
    // PHYSICAL_DEVICE_SWAPCHAIN_MAINTENANCE_1_FEATURES_KHR.
    private const int Features2Type = 1000059000;
    private const int Vulkan13Type = 53;
    private const int Maintenance1Type = 1000275000;

    // What the adapter has, required, optional; then the expected enabled set, worked out by hand.
    public static TheoryData<long, long, long, long> Cases => new()
    {
        { 0, 0, 0, 0 },
        { (long)All, 0, 0, 0 },
        { (long)All, 0, (long)All, (long)All },
        { 0, 0, (long)All, 0 },
        { (long)SA, (long)SA, 0, (long)SA },
        { (long)(SA | BC), 0, (long)(SA | Astc), (long)SA },
        { (long)TS, (long)TS, (long)BC, (long)TS },
        { (long)All, (long)SA, (long)TS, (long)(SA | TS) },
        { (long)(BC | Astc), (long)BC, (long)(Astc | SA), (long)(BC | Astc) },
        { (long)Astc, 0, (long)Astc, (long)Astc },
        { (long)TS, 0, (long)TS, (long)TS },
        { (long)All, (long)All, 0, (long)All },
        { (long)All, (long)Undefined, (long)(All | Undefined), (long)All },
        { 0, (long)Undefined, (long)Undefined, 0 },
    };

    // The chosen family's timestampValidBits and the timestamp period; then whether TimestampQueries is present.
    public static TheoryData<int, float, bool> Timestamps => new()
    {
        { 64, 1.0f, true },
        { 1, float.Epsilon, true },
        { 0, 1.0f, false },
        { -1, 1.0f, false },
        { 64, 0f, false },
        { 64, -1.0f, false },
        { 64, float.NaN, false },
        { 64, float.PositiveInfinity, false },
    };

    public static TheoryData<long> EachFeature => new() { (long)SA, (long)TS, (long)BC, (long)Astc };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Negotiate_Cases(long has, long required, long optional, long expected)
    {
        AdapterCandidate candidate = Having((GraphicsFeatures)has);

        FeatureChain.NegotiatedFeatures negotiated = FeatureChain.Negotiate(candidate, 11, (GraphicsFeatures)required, (GraphicsFeatures)optional);

        Assert.Equal((GraphicsFeatures)expected, negotiated.Enabled);
    }

    [Theory]
    [MemberData(nameof(Timestamps))]
    public void Negotiate_Timestamps_NeedBitsAndPeriod(int bits, float period, bool present)
    {
        AdapterCandidate candidate = Having(All) with { TimestampPeriod = period, QueueFamilies = new[] { Candidates.Family(11, true, true, true, bits) } };

        Assert.Equal(present ? All : All & ~TS, FeatureChain.Negotiate(candidate, 11, GraphicsFeatures.None, All).Enabled);
    }

    [Fact]
    public void Negotiate_Timestamps_ReadTheChosenFamily()
    {
        // Indices differ from positions, and only the second family has timestamp bits.
        AdapterCandidate candidate = Having(TS) with
        {
            QueueFamilies = new[] { Candidates.Family(11, true, true, true, 0), Candidates.Family(4, true, true, true, 64) },
        };

        Assert.Equal((GraphicsFeatures.None, TS), (FeatureChain.Negotiate(candidate, 11, 0, TS).Enabled, FeatureChain.Negotiate(candidate, 4, 0, TS).Enabled));
    }

    [Theory]
    [MemberData(nameof(EachFeature))]
    public void Negotiate_RequiredMissing_Throws(long feature)
    {
        AdapterCandidate candidate = Having(All & ~(GraphicsFeatures)feature);

        ArgumentException error = Assert.Throws<ArgumentException>("required", () => FeatureChain.Negotiate(candidate, 11, (GraphicsFeatures)feature, 0));
        Assert.StartsWith("The adapter lacks a required feature", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Negotiate_UnknownFamily_Throws()
    {
        // The family's position (1) isn't its index (11).
        Assert.Throws<ArgumentException>("queueFamily", () => FeatureChain.Negotiate(Having(All), 1, 0, 0));
    }

    [Theory]
    [InlineData(0L, false)]
    [InlineData((long)Candidates.AllFeatures, false)]
    [InlineData((long)GraphicsFeatures.SamplerAnisotropy, true)]
    [InlineData((long)(GraphicsFeatures.TextureCompressionBC | GraphicsFeatures.TextureCompressionAstc), true)]
    public void Fill_WritesTheChain(long enabled, bool maintenance1)
    {
        FeatureChain.Storage storage;

        // Garbage first: Fill must overwrite every structure.
        new Span<byte>(&storage, sizeof(FeatureChain.Storage)).Fill(0xFF);
        VkPhysicalDeviceFeatures2* head = FeatureChain.Fill(&storage, new FeatureChain.NegotiatedFeatures((GraphicsFeatures)enabled), maintenance1);

        Assert.True(head == &storage.Features2);
        AssertChain(head, (GraphicsFeatures)enabled, maintenance1);
        if (!maintenance1)
        {
            // Unlinked, but still overwritten: nothing points at it.
            Assert.Equal(Maintenance1Type, *(int*)&storage.SwapchainMaintenance1);
        }
    }

    [Fact]
    public void Fill_NullStorage_Throws() =>
        Assert.Throws<ArgumentNullException>("storage", () => FeatureChain.Fill(null, default, swapchainMaintenance1: false));

    /// <summary>
    /// Checks a written chain: the core features carry exactly the enabled flags, then the 1.3 features with only
    /// dynamicRendering and synchronization2, then the maintenance1 feature when used and nothing after it.
    /// </summary>
    internal static void AssertChain(VkPhysicalDeviceFeatures2* head, GraphicsFeatures enabled, bool maintenance1)
    {
        // sType is BRUTAL-internal: read it at offset 0.
        Assert.Equal(Features2Type, *(int*)head);
        VkPhysicalDeviceFeatures core = head->Features;
        Assert.Equal(
            ((enabled & SA) != 0, (enabled & BC) != 0, (enabled & Astc) != 0),
            ((bool)core.SamplerAnisotropy, (bool)core.TextureCompressionBC, (bool)core.TextureCompressionASTCLDR));
        core.SamplerAnisotropy = false;
        core.TextureCompressionBC = false;
        core.TextureCompressionASTCLDR = false;
        Assert.True(new ReadOnlySpan<byte>(&core, sizeof(VkPhysicalDeviceFeatures)).IndexOfAnyExcept((byte)0) < 0, "another core feature is set");

        var vulkan13 = (VkPhysicalDeviceVulkan13Features*)head->Next;
        Assert.True(vulkan13 is not null);
        Assert.Equal(Vulkan13Type, *(int*)vulkan13);
        Assert.True(vulkan13->DynamicRendering && vulkan13->Synchronization2);
        VkPhysicalDeviceVulkan13Features rest = *vulkan13;
        rest.Next = null;
        rest.DynamicRendering = false;
        rest.Synchronization2 = false;
        Assert.True(new ReadOnlySpan<byte>((byte*)&rest + 16, Unsafe.SizeOf<VkPhysicalDeviceVulkan13Features>() - 16).IndexOfAnyExcept((byte)0) < 0, "another 1.3 feature is set");

        var next = (VkPhysicalDeviceSwapchainMaintenance1FeaturesKHR*)vulkan13->Next;
        Assert.Equal(maintenance1, next is not null);
        if (maintenance1)
        {
            Assert.Equal(Maintenance1Type, *(int*)next);
            Assert.True(next->SwapchainMaintenance1);
            Assert.True(next->Next is null);
        }
    }

    // A candidate whose features are exactly `has`: timestamps through family 11's bits, with a valid period.
    private static AdapterCandidate Having(GraphicsFeatures has) => Candidates.Suitable() with
    {
        SamplerAnisotropy = (has & SA) != 0,
        TextureCompressionBC = (has & BC) != 0,
        TextureCompressionAstcLdr = (has & Astc) != 0,
        TimestampPeriod = 1.0f,
        QueueFamilies = new[]
        {
            Candidates.Family(10, graphics: false, compute: false, present: false, timestampBits: 64),
            Candidates.Family(11, graphics: true, compute: true, present: true, timestampBits: (has & TS) != 0 ? 64 : 0),
        },
    };
}
