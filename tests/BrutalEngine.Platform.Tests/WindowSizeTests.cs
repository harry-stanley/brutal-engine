using CsCheck;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class WindowSizeTests
{
    [Fact]
    public void Default_IsZeroByZero()
    {
        WindowSize size = default;

        Assert.Equal(0, size.Width);
        Assert.Equal(0, size.Height);
        Assert.Equal(new WindowSize(0, 0), size);
    }

    [Fact]
    public void Constructor_StoresWidthAndHeight()
    {
        WindowSize size = new(1920, 1080);

        Assert.Equal(1920, size.Width);
        Assert.Equal(1080, size.Height);
    }

    [Fact]
    public void Equality_ComparesWidthAndHeight()
    {
        Gen.Select(Gen.Int[0, 2], Gen.Int[0, 2], Gen.Int[0, 2], Gen.Int[0, 2]).Sample((w1, h1, w2, h2) =>
        {
            WindowSize left = new(w1, h1);
            WindowSize right = new(w2, h2);
            bool expected = w1 == w2 && h1 == h2;

            Assert.Equal(expected, left == right);
            Assert.Equal(!expected, left != right);
            if (expected)
            {
                Assert.Equal(left.GetHashCode(), right.GetHashCode());
            }
        });
    }
}
