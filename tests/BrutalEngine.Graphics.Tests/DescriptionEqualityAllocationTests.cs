using System;
using BrutalEngine.Core;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>
/// The descriptions document that no member allocates except <c>ToString</c>: equality, the operators and the hash
/// are measured here (docs/design/graphics.md section 5). The project turns background garbage collection off, which
/// AllocationMeasurementTests guards, so the counts are exact.
/// </summary>
public sealed class DescriptionEqualityAllocationTests
{
    private const int Iterations = 1_000;

    [Fact]
    public void BufferDescription_EqualityAndHash_DoNotAllocate()
    {
        var a = new BufferDescription { Size = 256, Usage = BufferUsage.Vertex, Memory = MemoryIntent.Upload, DebugName = "a" };
        BufferDescription b = a with { DebugName = new string("a".AsSpan()) };
        BufferDescription c = a with { Size = 512 };

        Assert.Equal(0, Measure(a, b, c, static (x, y) => x == y, static (x, y) => x != y));
    }

    [Fact]
    public void TextureDescription_EqualityAndHash_DoNotAllocate()
    {
        var a = new TextureDescription { Format = TextureFormat.Rgba8Unorm, Width = 64, Height = 32, Usage = TextureUsage.Sampled, DebugName = "a" };
        TextureDescription b = a with { DebugName = new string("a".AsSpan()) };
        TextureDescription c = a with { MipLevels = 2 };

        Assert.Equal(0, Measure(a, b, c, static (x, y) => x == y, static (x, y) => x != y));
    }

    [Fact]
    public void TextureViewDescription_EqualityAndHash_DoNotAllocate()
    {
        var a = new TextureViewDescription { Texture = new TextureHandle(new Handle(1, 1)), DebugName = "a" };
        TextureViewDescription b = a with { DebugName = new string("a".AsSpan()) };
        TextureViewDescription c = a with { BaseArrayLayer = 1 };

        Assert.Equal(0, Measure(a, b, c, static (x, y) => x == y, static (x, y) => x != y));
    }

    [Fact]
    public void SamplerDescription_EqualityAndHash_DoNotAllocate()
    {
        var a = new SamplerDescription { MaxAnisotropy = 4, Compare = CompareFunction.Less, DebugName = "a" };
        SamplerDescription b = a with { DebugName = new string("a".AsSpan()) };
        SamplerDescription c = a with { Compare = null };

        Assert.Equal(0, Measure(a, b, c, static (x, y) => x == y, static (x, y) => x != y));
    }

    // a and b are equal (with separately built debug names), c differs from both. The operators come in as static
    // lambdas (cached delegates, created during the warm-up pass), so no generic code boxes a description. Returns the
    // bytes this thread allocated over the measured loop; assertions run outside it, on the accumulated results.
    private static long Measure<T>(T a, T b, T c, Func<T, T, bool> equal, Func<T, T, bool> notEqual)
        where T : struct, IEquatable<T>
    {
        bool correct = Exercise(a, b, c, equal, notEqual);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            correct &= Exercise(a, b, c, equal, notEqual);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(correct, "Equality gave a wrong answer inside the measured loop.");
        return allocated;
    }

    private static bool Exercise<T>(T a, T b, T c, Func<T, T, bool> equal, Func<T, T, bool> notEqual)
        where T : struct, IEquatable<T>
    {
        return a.Equals(b) && !a.Equals(c) && equal(a, b) && !equal(a, c) && notEqual(a, c) && !notEqual(a, b)
            && a.GetHashCode() == b.GetHashCode();
    }
}
