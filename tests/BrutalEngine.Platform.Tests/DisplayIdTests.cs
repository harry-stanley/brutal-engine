using CsCheck;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class DisplayIdTests
{
    [Fact]
    public void Default_IsInvalidZero()
    {
        DisplayId id = default;

        Assert.Equal(0u, id.Value);
        Assert.Equal(new DisplayId(0), id);
        Assert.NotEqual(new DisplayId(1), id);
    }

    [Fact]
    public void Constructor_StoresValue()
    {
        Assert.Equal(uint.MaxValue, new DisplayId(uint.MaxValue).Value);
    }

    [Fact]
    public void Equality_ComparesValue()
    {
        Gen.Select(Gen.UInt[0, 3], Gen.UInt[0, 3]).Sample((a, b) =>
        {
            DisplayId left = new(a);
            DisplayId right = new(b);

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
