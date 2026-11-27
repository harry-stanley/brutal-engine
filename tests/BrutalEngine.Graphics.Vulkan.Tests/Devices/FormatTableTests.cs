using System;
using Brutal.VulkanApi;
using BrutalEngine.Graphics.Vulkan.Devices;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Devices;

public sealed class FormatTableTests
{
    private const VkFormatFeatureFlags Color = VkFormatFeatureFlags.ColorAttachmentBit;
    private const VkFormatFeatureFlags Depth = VkFormatFeatureFlags.DepthStencilAttachmentBit;
    private const VkSampleCountFlags X1 = VkSampleCountFlags._1Bit;
    private const VkSampleCountFlags X2 = VkSampleCountFlags._2Bit;
    private const VkSampleCountFlags X4 = VkSampleCountFlags._4Bit;
    private const VkSampleCountFlags X8 = VkSampleCountFlags._8Bit;
    private const VkSampleCountFlags X16 = VkSampleCountFlags._16Bit;

    // The note's table (docs/design/graphics-vulkan.md section 3, "Capabilities"), with BRUTAL's VkFormat names.
    internal static (TextureFormat Format, VkFormat Vk)[] Pairs =>
    [
        (TextureFormat.R8Unorm, VkFormat.R8UNorm),
        (TextureFormat.Rg8Unorm, VkFormat.R8G8UNorm),
        (TextureFormat.Rgba8Unorm, VkFormat.R8G8B8A8UNorm),
        (TextureFormat.Rgba8UnormSrgb, VkFormat.R8G8B8A8SRGB),
        (TextureFormat.Bgra8Unorm, VkFormat.B8G8R8A8UNorm),
        (TextureFormat.Bgra8UnormSrgb, VkFormat.B8G8R8A8SRGB),
        (TextureFormat.R16Float, VkFormat.R16SFloat),
        (TextureFormat.Depth16Unorm, VkFormat.D16UNorm),
        (TextureFormat.Rg16Float, VkFormat.R16G16SFloat),
        (TextureFormat.Rgba16Float, VkFormat.R16G16B16A16SFloat),
        (TextureFormat.R32Float, VkFormat.R32SFloat),
        (TextureFormat.Rg32Float, VkFormat.R32G32SFloat),
        (TextureFormat.Rgba32Float, VkFormat.R32G32B32A32SFloat),
        (TextureFormat.R32Uint, VkFormat.R32UInt),
        (TextureFormat.Rgba8Uint, VkFormat.R8G8B8A8UInt),
        (TextureFormat.Depth32Float, VkFormat.D32SFloat),
    ];

    public static TheoryData<TextureFormat, VkFormat> Table => new(Pairs);

    // Each feature bit alone, from the note's list, and bits that map to nothing.
    internal static (VkFormatFeatureFlags Bit, FormatSupport Flag)[] BitPairs =>
    [
        (VkFormatFeatureFlags.SampledImageBit, FormatSupport.Sampled),
        (VkFormatFeatureFlags.SampledImageFilterLinearBit, FormatSupport.Filterable),
        (VkFormatFeatureFlags.ColorAttachmentBit, FormatSupport.ColorTarget),
        (VkFormatFeatureFlags.ColorAttachmentBlendBit, FormatSupport.Blendable),
        (VkFormatFeatureFlags.DepthStencilAttachmentBit, FormatSupport.DepthStencilTarget),
        (VkFormatFeatureFlags.StorageImageBit, FormatSupport.Storage),
        (VkFormatFeatureFlags.TransferSrcBit, FormatSupport.CopySource),
        (VkFormatFeatureFlags.TransferDstBit, FormatSupport.CopyDestination),
        (VkFormatFeatureFlags.StorageImageAtomicBit, FormatSupport.None),
        (VkFormatFeatureFlags.BlitSrcBit, FormatSupport.None),
        (VkFormatFeatureFlags.BlitDstBit, FormatSupport.None),
        (VkFormatFeatureFlags.VertexBufferBit, FormatSupport.None),
        (VkFormatFeatureFlags.SampledImageFilterMinmaxBit, FormatSupport.None),
    ];

    public static TheoryData<VkFormatFeatureFlags, FormatSupport> Bits => new(BitPairs);

    // Features, the format's counts, the counts it must have, and whether it multisamples.
    public static TheoryData<VkFormatFeatureFlags, VkSampleCountFlags, VkSampleCountFlags, bool> Multisample => new()
    {
        { Color, X1 | X4, X1 | X4, true },
        { Color, X1 | X16, X1 | X16, true },
        { Depth, X1 | X2, VkSampleCountFlags.None, true },
        { Color, X1, X1, false },
        { Depth, X1, VkSampleCountFlags.None, false },
        { Color, X1 | VkSampleCountFlags._32Bit, X1, false },
        { Color, X1 | X4, X1 | X4 | X8, false },
        { Color, X1 | X4, X1, false },
        { VkFormatFeatureFlags.SampledImageBit | VkFormatFeatureFlags.StorageImageBit, X1 | X4, VkSampleCountFlags.None, false },
    };

    // Colour, depth and integer counts, then whether the other colour formats, the integer formats (R32Uint, Rgba8Uint)
    // and the depth formats (Depth16Unorm, Depth32Float) multisample.
    public static TheoryData<VkSampleCountFlags, VkSampleCountFlags, VkSampleCountFlags, bool, bool, bool> Counts => new()
    {
        { X1 | X4 | X8, X1 | X2, X1 | X4, true, false, true },
        { X1 | X4 | X8, X1, X1 | X4 | X8 | X16, true, true, false },
        { X1, X1 | X4, X1 | X4, false, false, true },
        { X1 | X4, X1 | X4, X1, true, false, true },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Map_EveryFormat_RoundTrips(TextureFormat format, VkFormat vk)
    {
        Assert.Equal(vk, FormatTable.ToVk(format));
        Assert.Equal(format, FormatTable.FromVk(vk));
    }

    [Fact]
    public void Map_Table_CoversEveryFormatButUndefined()
    {
        Assert.Equal(Enum.GetValues<TextureFormat>().Length - 1, Pairs.Length);
        Assert.Equal(16, FormatTable.Count);
    }

    [Theory]
    [InlineData(TextureFormat.Undefined)]
    [InlineData((TextureFormat)(-1))]
    [InlineData((TextureFormat)17)]
    public void ToVk_NotATableFormat_Throws(TextureFormat format) =>
        Assert.Throws<ArgumentOutOfRangeException>(nameof(format), () => FormatTable.ToVk(format));

    [Theory]
    [InlineData(VkFormat.Undefined)]
    [InlineData(VkFormat.R8G8B8A8SNorm)]
    [InlineData(VkFormat.D24UNormS8UInt)]
    public void FromVk_NotInTheTable_IsUndefined(VkFormat format) =>
        Assert.Equal(TextureFormat.Undefined, FormatTable.FromVk(format));

    [Theory]
    [MemberData(nameof(Bits))]
    public void Support_FromFeatureBits(VkFormatFeatureFlags bit, FormatSupport expected)
    {
        Assert.Equal(expected, FormatTable.Support(bit, X1, X1));

        // The bit added to every other mapped bit adds exactly its own flag.
        const VkFormatFeatureFlags others = VkFormatFeatureFlags.SampledImageBit | VkFormatFeatureFlags.TransferDstBit;
        FormatSupport baseline = FormatTable.Support(others, X1, X1);
        Assert.Equal(FormatSupport.Sampled | FormatSupport.CopyDestination, baseline);
        Assert.Equal(baseline | expected, FormatTable.Support(others | bit, X1, X1));
    }

    [Theory]
    [MemberData(nameof(Multisample))]
    public void Support_SampleCounts_DecideMultisample(VkFormatFeatureFlags features, VkSampleCountFlags counts, VkSampleCountFlags required, bool expected)
    {
        FormatSupport support = FormatTable.Support(features, counts, required);

        Assert.Equal(expected, (support & FormatSupport.Multisample) != 0);
        Assert.Equal(FormatTable.Support(features, X1, X1), support & ~FormatSupport.Multisample);
    }

    [Theory]
    [MemberData(nameof(Counts))]
    public void Build_EachFormat_UsesItsOwnFeaturesAndCounts(VkSampleCountFlags color, VkSampleCountFlags depth, VkSampleCountFlags integerCounts, bool colorFormats, bool integerFormats, bool depthFormats)
    {
        ScriptedAdapter adapter = new() { IntegerColorSampleCounts = integerCounts };
        adapter.Limits.FramebufferColorSampleCounts = color;
        adapter.Limits.FramebufferDepthSampleCounts = depth;
        adapter.Limits.SampledImageColorSampleCounts = X1 | X2 | X4 | X8 | X16;
        foreach ((TextureFormat format, VkFormat vk) in Pairs)
        {
            adapter.Features[vk] = FeaturesOf(format);
        }

        FormatSupport[] support = FormatTable.Build(adapter, adapter.Limits, integerCounts);

        Assert.Equal(17, support.Length);
        Assert.Equal(FormatSupport.None, support[0]);
        Assert.Equal(Pairs.Length, adapter.FormatQueries.Count);
        foreach ((TextureFormat format, VkFormat _) in Pairs)
        {
            bool multisample = format is TextureFormat.Depth16Unorm or TextureFormat.Depth32Float ? depthFormats
                : format is TextureFormat.R32Uint or TextureFormat.Rgba8Uint ? integerFormats
                : colorFormats;
            FormatSupport expected = FormatTable.Support(FeaturesOf(format), X1, X1) | (multisample ? FormatSupport.Multisample : FormatSupport.None);
            Assert.Equal(expected, support[(int)format]);
        }
    }

    [Fact]
    public void Build_AllocatesOnlyTheArray()
    {
        ScriptedAdapter adapter = new() { IntegerColorSampleCounts = X1 | X4 };
        adapter.Limits.FramebufferColorSampleCounts = X1 | X4;
        _ = FormatTable.Build(adapter, adapter.Limits, X1 | X4);
        adapter.FormatQueries.Capacity = 64;

        long before = GC.GetAllocatedBytesForCurrentThread();
        var reference = new FormatSupport[FormatTable.Count + 1];
        long arrayBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        FormatSupport[] support = FormatTable.Build(adapter, adapter.Limits, X1 | X4);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(arrayBytes, allocated);
        Assert.Equal(reference.Length, support.Length);
    }

    // A different feature set per format (the format's value picks which mapped bits it has), with both attachment bits
    // on the formats that are targets, so a query for the wrong format or a write to the wrong entry shows.
    private static VkFormatFeatureFlags FeaturesOf(TextureFormat format)
    {
        int value = (int)format;
        VkFormatFeatureFlags features = format is TextureFormat.Depth16Unorm or TextureFormat.Depth32Float ? Depth : Color;
        features |= (value & 1) != 0 ? VkFormatFeatureFlags.SampledImageBit : 0;
        features |= (value & 2) != 0 ? VkFormatFeatureFlags.SampledImageFilterLinearBit : 0;
        features |= (value & 4) != 0 ? VkFormatFeatureFlags.TransferSrcBit : 0;
        features |= (value & 8) != 0 ? VkFormatFeatureFlags.StorageImageBit : 0;
        return features;
    }
}
