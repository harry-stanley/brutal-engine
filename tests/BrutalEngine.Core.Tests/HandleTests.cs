using System;
using CsCheck;
using Xunit;

namespace BrutalEngine.Core.Tests;

public sealed class HandleTests
{
    [Fact]
    public void Default_IsNull()
    {
        Handle handle = default;

        Assert.True(handle.IsNull);
        Assert.Equal(Handle.Null, handle);
        Assert.Equal(0u, handle.Index);
        Assert.Equal(0u, handle.Generation);
        Assert.Equal(0ul, handle.RawValue);
    }

    [Fact]
    public void Constructor_StoresIndexAndGeneration()
    {
        Handle handle = new(uint.MaxValue, uint.MaxValue);

        Assert.Equal(uint.MaxValue, handle.Index);
        Assert.Equal(uint.MaxValue, handle.Generation);
        Assert.False(handle.IsNull);
    }

    [Fact]
    public void Constructor_GenerationZero_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Handle(5, 0));
    }

    [Fact]
    public void IndexZeroWithNonZeroGeneration_IsNotNull()
    {
        Handle handle = new(0, 1);

        Assert.False(handle.IsNull);
        Assert.NotEqual(Handle.Null, handle);
    }

    [Fact]
    public void RawValue_PacksGenerationHighAndIndexLow()
    {
        Handle handle = new(0x1234_5678, 0x9ABC_DEF0);

        Assert.Equal(0x9ABC_DEF0_1234_5678ul, handle.RawValue);
    }

    [Fact]
    public void FromRawValue_Zero_IsNull()
    {
        Assert.True(Handle.FromRawValue(0).IsNull);
    }

    [Fact]
    public void FromRawValue_GenerationZeroWithIndex_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Handle.FromRawValue(7));
    }

    [Fact]
    public void RawValue_RoundTrips_ForAnyValidHandle()
    {
        Gen.Select(Gen.UInt, Gen.UInt[1, uint.MaxValue]).Sample((index, generation) =>
        {
            Handle handle = new(index, generation);
            var restored = Handle.FromRawValue(handle.RawValue);

            return restored == handle && restored.Index == index && restored.Generation == generation;
        });
    }

    [Fact]
    public void Equality_IsByIndexAndGeneration()
    {
        Handle a = new(3, 1);
        Handle same = new(3, 1);
        Handle otherGeneration = new(3, 2);
        Handle otherIndex = new(4, 1);

        Assert.True(a == same);
        Assert.True(a.Equals(same));
        Assert.True(a.Equals((object)same));
        Assert.Equal(a.GetHashCode(), same.GetHashCode());
        Assert.True(a != otherGeneration);
        Assert.True(a != otherIndex);
        Assert.False(a.Equals((object)"not a handle"));
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void ToString_DescribesHandle()
    {
        Assert.Equal("Handle(null)", Handle.Null.ToString());
        Assert.Equal("Handle(12:3)", new Handle(12, 3).ToString());
    }
}
