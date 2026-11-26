using System;
using System.Linq;
using System.Runtime.CompilerServices;
using BrutalEngine.Graphics.Backend;
using Xunit;

namespace BrutalEngine.Graphics.Tests.Backend;

/// <summary>
/// The backend interface's value types (docs/design/graphics.md, "The backend interface"): which are unmanaged, and their
/// enum values.
/// </summary>
public sealed class BackendTypeTests
{
    public static TheoryData<string> UnmanagedTypes() =>
        [nameof(ResourceTransition)];

    [Theory]
    [MemberData(nameof(UnmanagedTypes))]
    public void ValueTypes_AreUnmanaged(string type)
    {
        (Type clrType, bool containsReferences) = type switch
        {
            nameof(ResourceTransition) => (typeof(ResourceTransition), RuntimeHelpers.IsReferenceOrContainsReferences<ResourceTransition>()),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown type."),
        };

        Assert.True(clrType.IsValueType);
        Assert.False(containsReferences);
    }

    public static TheoryData<string> Enums() =>
        [nameof(ResourceState)];

    [Theory]
    [MemberData(nameof(Enums))]
    public void EnumValues_MatchDesign(string type)
    {
        // Written from docs/design/graphics.md's backend interface block, in its order, numbered from 0.
        (Type enumType, (string Name, int Value)[] expected) = type switch
        {
            nameof(ResourceState) => (
                typeof(ResourceState),
                new[]
                {
                    ("Undefined", 0), ("CopySource", 1), ("CopyDestination", 2), ("VertexBuffer", 3), ("IndexBuffer", 4),
                    ("IndirectBuffer", 5), ("UniformRead", 6), ("StorageRead", 7), ("StorageReadWrite", 8), ("SampledRead", 9),
                    ("ColorTarget", 10), ("DepthRead", 11), ("DepthReadWrite", 12), ("Present", 13),
                }),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown enum."),
        };

        Assert.False(enumType.IsDefined(typeof(FlagsAttribute), inherit: false));
        Assert.Equal(typeof(int), Enum.GetUnderlyingType(enumType));
        Assert.Equal(expected, Enum.GetValuesAsUnderlyingType(enumType).Cast<int>().Select(v => (Enum.GetName(enumType, v)!, v)));
    }
}
