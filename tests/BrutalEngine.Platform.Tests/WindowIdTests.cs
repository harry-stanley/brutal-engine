using CsCheck;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class WindowIdTests
{
    [Fact]
    public void Default_MeansNoWindow()
    {
        WindowId id = default;

        Assert.Equal(0u, id.Value);
        Assert.Equal(new WindowId(0), id);
        Assert.NotEqual(new WindowId(1), id);
    }

    [Fact]
    public void Constructor_StoresValue()
    {
        Assert.Equal(uint.MaxValue, new WindowId(uint.MaxValue).Value);
    }

    [Fact]
    public void Equality_ComparesValue()
    {
        Gen.Select(Gen.UInt[0, 3], Gen.UInt[0, 3]).Sample((a, b) =>
        {
            WindowId left = new(a);
            WindowId right = new(b);

            Assert.Equal(a == b, left == right);
            Assert.Equal(a != b, left != right);
            Assert.Equal(a == b, left.Equals((object)right));
            if (a == b)
            {
                Assert.Equal(left.GetHashCode(), right.GetHashCode());
            }
        });
    }
}
