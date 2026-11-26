using System;
using System.Runtime.CompilerServices;
using CsCheck;
using Xunit;

namespace BrutalEngine.World.Tests;

public sealed class EntityTests
{
    [Fact]
    public void Default_IsNull()
    {
        Entity entity = default;

        Assert.True(entity.IsNull);
        Assert.True(entity == Entity.Null);
        Assert.Equal(0u, entity.Index);
        Assert.Equal(0u, entity.Generation);
        Assert.Equal(0ul, entity.RawValue);
    }

    [Fact]
    public void Constructor_StoresIndexAndGeneration()
    {
        Entity entity = new(uint.MaxValue, uint.MaxValue);

        Assert.Equal(uint.MaxValue, entity.Index);
        Assert.Equal(uint.MaxValue, entity.Generation);
        Assert.False(entity.IsNull);
    }

    [Fact]
    public void IndexZeroWithGenerationOne_IsNotNull()
    {
        Entity entity = new(0, 1);

        Assert.False(entity.IsNull);
        Assert.True(entity != Entity.Null);
    }

    [Fact]
    public void GenerationZero_Throws()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => new Entity(12, 0));

        Assert.Equal("generation", exception.ParamName);
    }

    [Fact]
    public void RawValue_PacksGenerationHighAndIndexLow()
    {
        Entity entity = new(0x1234_5678, 0x9ABC_DEF0);

        Assert.Equal(0x9ABC_DEF0_1234_5678ul, entity.RawValue);
    }

    [Fact]
    public void RawValue_RoundTrips()
    {
        Gen.Select(Gen.UInt, Gen.UInt[1, uint.MaxValue]).Sample((index, generation) =>
        {
            Entity entity = new(index, generation);
            var restored = Entity.FromRawValue(entity.RawValue);

            return restored == entity && restored.Index == index && restored.Generation == generation
                && restored.RawValue == entity.RawValue;
        });
    }

    [Fact]
    public void FromRawValue_Zero_IsNull()
    {
        Assert.True(Entity.FromRawValue(0).IsNull);
    }

    [Theory]
    [InlineData(1ul)]
    [InlineData(12ul)]
    [InlineData(0xFFFF_FFFFul)]
    public void FromRawValue_NonCanonical_Throws(ulong rawValue)
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(() => Entity.FromRawValue(rawValue));

        Assert.Equal("rawValue", exception.ParamName);
        Assert.Equal(rawValue, exception.ActualValue);
    }

    [Fact]
    public void Equality_IsByIndexAndGeneration()
    {
        Gen.Select(Gen.UInt[0, 3], Gen.UInt[1, 3], Gen.UInt[0, 3], Gen.UInt[1, 3]).Sample((i1, g1, i2, g2) =>
        {
            Entity a = new(i1, g1);
            Entity b = new(i2, g2);
            bool same = i1 == i2 && g1 == g2;

            return a.Equals(b) == same && (a == b) == same && (a != b) == !same && a.Equals((object)b) == same
                && (!same || a.GetHashCode() == b.GetHashCode());
        });
    }

    [Fact]
    public void Equals_OtherType_False()
    {
        Entity entity = new(1, 1);

        Assert.False(entity.Equals(entity.RawValue));
        Assert.False(entity.Equals(null));
    }

    [Theory]
    [InlineData(12u, 3u, "Entity(12v3)")]
    [InlineData(0u, 1u, "Entity(0v1)")]
    [InlineData(uint.MaxValue, uint.MaxValue, "Entity(4294967295v4294967295)")]
    public void ToString_Formats(uint index, uint generation, string expected)
    {
        Assert.Equal(expected, new Entity(index, generation).ToString());
    }

    [Fact]
    public void ToString_Null_Formats()
    {
        Assert.Equal("Entity(null)", Entity.Null.ToString());
    }

    [Fact]
    public void Layout_IsEightUnmanagedBytes()
    {
        Assert.Equal(8, SizeOfUnmanaged<Entity>());
    }

    // The unmanaged constraint makes this a compile-time check that Entity can be stored inside components.
    private static int SizeOfUnmanaged<T>()
        where T : unmanaged => Unsafe.SizeOf<T>();
}
