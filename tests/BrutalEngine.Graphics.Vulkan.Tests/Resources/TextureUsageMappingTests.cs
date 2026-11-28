using System;
using BrutalEngine.Graphics.Vulkan.Resources;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

public sealed class TextureUsageMappingTests
{
    // Each flag's value from src/BrutalEngine.Graphics/TextureUsage.cs, and its Vulkan bit from BRUTAL's generated enum
    // (Brutal.Vulkan/Generated/Enums/VkImageUsageFlags.gen.cs:77-102): TRANSFER_SRC 0x1, TRANSFER_DST 0x2, SAMPLED 0x4,
    // STORAGE 0x8, COLOR_ATTACHMENT 0x10, DEPTH_STENCIL_ATTACHMENT 0x20.
    private static readonly (int Usage, uint Bit)[] _table =
    [
        (1 << 0, 0x4),  // Sampled
        (1 << 1, 0x8),  // Storage
        (1 << 2, 0x10), // ColorTarget
        (1 << 3, 0x20), // DepthStencilTarget
        (1 << 4, 0x1),  // CopySource
        (1 << 5, 0x2),  // CopyDestination
    ];

    public static TheoryData<TextureUsage, uint> EachFlag => new()
    {
        { TextureUsage.Sampled, 0x4 },
        { TextureUsage.Storage, 0x8 },
        { TextureUsage.ColorTarget, 0x10 },
        { TextureUsage.DepthStencilTarget, 0x20 },
        { TextureUsage.CopySource, 0x1 },
        { TextureUsage.CopyDestination, 0x2 },
    };

    [Theory]
    [MemberData(nameof(EachFlag))]
    public void Map_EachFlag(TextureUsage usage, uint expected)
    {
        Assert.Equal(expected, (uint)TextureUsageMapping.Map(usage));
    }

    [Fact]
    public void Map_EveryCombination_IsTheUnionOfItsBits()
    {
        for (int usage = 0; usage < 1 << _table.Length; usage++)
        {
            uint expected = 0;
            foreach ((int flag, uint bit) in _table)
            {
                expected |= (usage & flag) != 0 ? bit : 0;
            }

            Assert.Equal(expected, (uint)TextureUsageMapping.Map((TextureUsage)usage));
        }
    }

    // The first bit above the last flag, the sign bit, and every bit together with a defined flag.
    [Theory]
    [InlineData(1 << 6)]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData((1 << 6) | 1)]
    public void Map_UndefinedBit_Throws(int usage)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(() => TextureUsageMapping.Map((TextureUsage)usage));

        Assert.Equal("usage", error.ParamName);
        Assert.Equal(usage, error.ActualValue);
    }

    [Fact]
    public void Map_EveryCombination_AllocatesNothing()
    {
        uint checksum = Sum();

        long before = GC.GetAllocatedBytesForCurrentThread();
        checksum += Sum();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);

        // Each of the six bits is in half of the 64 combinations: 32 * 0x3F, twice.
        Assert.Equal(2u * 32 * 0x3F, checksum);
    }

    private static uint Sum()
    {
        uint sum = 0;
        for (int usage = 0; usage < 1 << _table.Length; usage++)
        {
            sum += (uint)TextureUsageMapping.Map((TextureUsage)usage);
        }

        return sum;
    }
}
