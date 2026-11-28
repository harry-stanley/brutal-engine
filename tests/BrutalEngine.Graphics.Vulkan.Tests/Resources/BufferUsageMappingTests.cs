using System;
using BrutalEngine.Graphics.Vulkan.Resources;
using Xunit;

namespace BrutalEngine.Graphics.Vulkan.Tests.Resources;

public sealed class BufferUsageMappingTests
{
    // Each flag's value from src/BrutalEngine.Graphics/BufferUsage.cs, and its Vulkan bit from BRUTAL's generated enum
    // (Brutal.Vulkan/Generated/Enums/VkBufferUsageFlags.gen.cs:81-121): TRANSFER_SRC 0x1, TRANSFER_DST 0x2,
    // UNIFORM_BUFFER 0x10, STORAGE_BUFFER 0x20, INDEX_BUFFER 0x40, VERTEX_BUFFER 0x80, INDIRECT_BUFFER 0x100.
    private static readonly (int Usage, uint Bit)[] _table =
    [
        (1 << 0, 0x80),  // Vertex
        (1 << 1, 0x40),  // Index
        (1 << 2, 0x10),  // Uniform
        (1 << 3, 0x20),  // Storage
        (1 << 4, 0x100), // Indirect
        (1 << 5, 0x1),   // CopySource
        (1 << 6, 0x2),   // CopyDestination
    ];

    public static TheoryData<BufferUsage, uint> EachFlag => new()
    {
        { BufferUsage.Vertex, 0x80 },
        { BufferUsage.Index, 0x40 },
        { BufferUsage.Uniform, 0x10 },
        { BufferUsage.Storage, 0x20 },
        { BufferUsage.Indirect, 0x100 },
        { BufferUsage.CopySource, 0x1 },
        { BufferUsage.CopyDestination, 0x2 },
    };

    [Theory]
    [MemberData(nameof(EachFlag))]
    public void Map_EachFlag(BufferUsage usage, uint expected)
    {
        Assert.Equal(expected, (uint)BufferUsageMapping.Map(usage));
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

            Assert.Equal(expected, (uint)BufferUsageMapping.Map((BufferUsage)usage));
        }
    }

    [Fact]
    public void Map_EveryCombination_AllocatesNothing()
    {
        uint checksum = 0;
        for (int usage = 0; usage < 1 << _table.Length; usage++)
        {
            checksum ^= (uint)BufferUsageMapping.Map((BufferUsage)usage);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int usage = 0; usage < 1 << _table.Length; usage++)
        {
            checksum ^= (uint)BufferUsageMapping.Map((BufferUsage)usage);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(0u, checksum);
    }

    [Fact]
    public void Map_None_IsNoBits()
    {
        Assert.Equal(0u, (uint)BufferUsageMapping.Map(BufferUsage.None));
    }

    [Theory]
    [InlineData(1 << 7)]
    [InlineData(1 << 30)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData((1 << 7) | 1)]
    public void Map_UndefinedBit_Throws(int usage)
    {
        ArgumentOutOfRangeException error = Assert.Throws<ArgumentOutOfRangeException>(nameof(usage), () => BufferUsageMapping.Map((BufferUsage)usage));

        Assert.Equal(usage, error.ActualValue);
    }

    [Fact]
    public void Table_CoversEveryDefinedFlag()
    {
        // A flag added to BufferUsage later must get a row here (and a bit in the mapping).
        Assert.Equal(_table.Length + 1, Enum.GetValues<BufferUsage>().Length);
    }
}
