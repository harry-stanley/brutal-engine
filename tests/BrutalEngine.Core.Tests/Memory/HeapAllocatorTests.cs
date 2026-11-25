using System;
using BrutalEngine.Core.Memory;
using CsCheck;
using Xunit;

namespace BrutalEngine.Core.Tests.Memory;

public sealed unsafe class HeapAllocatorTests
{
    [Fact]
    public void Allocate_ReturnsWritableMemory()
    {
        HeapAllocator heap = new();

        byte* block = (byte*)heap.Allocate(256, 16);
        try
        {
            Assert.True(block != null);
            new Span<byte>(block, 256).Fill(0xAB);
            Assert.Equal(0xAB, block[255]);
        }
        finally
        {
            heap.Free(block);
        }
    }

    [Fact]
    public void Allocate_HonoursAnyPowerOfTwoAlignment()
    {
        HeapAllocator heap = new();

        Gen.Select(Gen.Int[0, 12], Gen.Int[1, 4096]).Sample((shift, size) =>
        {
            nuint alignment = (nuint)1 << shift;
            void* block = heap.Allocate((nuint)size, alignment);
            bool aligned = (nuint)block % alignment == 0;
            heap.Free(block);
            return aligned;
        });
    }

    [Fact]
    public void Allocate_ZeroBytes_Throws()
    {
        HeapAllocator heap = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => heap.Allocate(0, 8));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(24u)]
    public void Allocate_NonPowerOfTwoAlignment_Throws(uint alignment)
    {
        HeapAllocator heap = new();

        Assert.Throws<ArgumentOutOfRangeException>(() => heap.Allocate(16, alignment));
    }

    [Fact]
    public void Free_Null_IsIgnored()
    {
        new HeapAllocator().Free(null);
    }

    [Fact]
    public void AllocateAndFree_DoNotAllocateManagedMemory()
    {
        HeapAllocator heap = new();
        heap.Free(heap.Allocate(64, 16));

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            heap.Free(heap.Allocate(64, 16));
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
