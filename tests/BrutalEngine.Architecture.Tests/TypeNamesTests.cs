using System;
using System.Collections.Generic;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

public sealed class TypeNamesTests
{
    [Theory]
    [InlineData(typeof(int), "System.Int32")]
    [InlineData(typeof(int[]), "System.Int32[]")]
    [InlineData(typeof(int[,]), "System.Int32[,]")]
    [InlineData(typeof(byte*), "System.Byte*")]
    [InlineData(typeof(List<int>), "System.Collections.Generic.List<System.Int32>")]
    [InlineData(typeof(Dictionary<string, List<int>>), "System.Collections.Generic.Dictionary<System.String, System.Collections.Generic.List<System.Int32>>")]
    [InlineData(typeof(Dictionary<,>), "System.Collections.Generic.Dictionary<TKey, TValue>")]
    [InlineData(typeof(Dictionary<int, string>.Enumerator), "System.Collections.Generic.Dictionary<TKey, TValue>.Enumerator")]
    [InlineData(typeof(Environment.SpecialFolder), "System.Environment.SpecialFolder")]
    public void Display_FormatsLikeCSharp(Type type, string expected)
    {
        Assert.Equal(expected, TypeNames.Display(type));
    }

    [Fact]
    public void Display_FunctionPointer_ListsParametersThenReturnType()
    {
        Type type = typeof(Fakes.LeaksFunctionPointer).GetField(nameof(Fakes.LeaksFunctionPointer.Callback))!.FieldType;

        Assert.Equal("delegate*<Brutal.FakeApi.FakeDevice, System.Void>", TypeNames.Display(type));
    }
}
