using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BrutalEngine.Core;
using CsCheck;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

/// <summary>Every GPU handle kind has the same shape and behaviour (docs/design/graphics.md section 1).</summary>
public sealed class HandleKindTests
{
    // The kind registry: one row per GPU handle kind. A new kind adds its row here and every test below covers it;
    // Registry_CoversEveryHandleKind fails until it does.
    private static readonly HandleKind[] _kinds =
    [
        new HandleKind<BufferHandle>("Buffer", h => new BufferHandle(h), x => x.Value, x => x.IsNull, (a, b) => a == b, (a, b) => a != b),
        new HandleKind<TextureHandle>("Texture", h => new TextureHandle(h), x => x.Value, x => x.IsNull, (a, b) => a == b, (a, b) => a != b),
        new HandleKind<TextureViewHandle>("TextureView", h => new TextureViewHandle(h), x => x.Value, x => x.IsNull, (a, b) => a == b, (a, b) => a != b),
        new HandleKind<SamplerHandle>("Sampler", h => new SamplerHandle(h), x => x.Value, x => x.IsNull, (a, b) => a == b, (a, b) => a != b),
        new HandleKind<ShaderModuleHandle>("ShaderModule", h => new ShaderModuleHandle(h), x => x.Value, x => x.IsNull, (a, b) => a == b, (a, b) => a != b),
        new HandleKind<SwapchainHandle>("Swapchain", h => new SwapchainHandle(h), x => x.Value, x => x.IsNull, (a, b) => a == b, (a, b) => a != b),
    ];

    // Indexes and generations from a tiny range, so equal handles are common; generation 0 is the null handle.
    private static readonly Gen<Handle> _handles =
        Gen.Select(Gen.UInt[0, 2], Gen.UInt[0, 2], (index, generation) => generation == 0 ? Handle.Null : new Handle(index, generation));

    public static TheoryData<string> KindNames()
    {
        TheoryData<string> data = [];
        foreach (HandleKind kind in _kinds)
        {
            data.Add(kind.Name);
        }

        return data;
    }

    public static TheoryData<string, uint, uint, string> ToStringCases()
    {
        TheoryData<string, uint, uint, string> data = [];
        foreach (HandleKind kind in _kinds)
        {
            data.Add(kind.Name, 0, 0, kind.Name + "(null)");
            data.Add(kind.Name, 0, 1, kind.Name + "(0:1)");
            data.Add(kind.Name, 12, 3, kind.Name + "(12:3)");
            data.Add(kind.Name, uint.MaxValue, uint.MaxValue, kind.Name + "(4294967295:4294967295)");
        }

        return data;
    }

    [Fact]
    public void Registry_CoversEveryHandleKind()
    {
        // A handle kind is any type in the assembly constructed from a Core handle.
        string[] kinds =
        [
            .. typeof(BufferHandle).Assembly.GetTypes()
                .Where(t => t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, [typeof(Handle)]) is not null)
                .Select(t => t.FullName!)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(_kinds.Select(k => k.Type.FullName!).Order(StringComparer.Ordinal), kinds);
    }

    [Fact]
    public void Registry_KindNameIsTheTypeNameWithoutHandle()
    {
        // ToString prints the kind name: the type name without its "Handle" suffix (graphics.md section 1).
        Assert.All(_kinds, k => Assert.Equal(k.Type.Name.EndsWith("Handle", StringComparison.Ordinal) ? k.Type.Name[..^"Handle".Length] : k.Type.Name, k.Name));
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public void Default_IsNull(string kind)
    {
        HandleKind k = Kind(kind);

        Assert.True(k.DefaultIsNull);
        Assert.Equal(Handle.Null, k.DefaultValue);
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public void IsNull_IssuedHandle_IsFalse(string kind)
    {
        HandleKind k = Kind(kind);

        Assert.False(k.IsNull(new Handle(0, 1)));
        Assert.False(k.IsNull(new Handle(uint.MaxValue, uint.MaxValue)));
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public void Constructor_KeepsTheCoreHandle(string kind)
    {
        Handle value = new(7, 3);

        Assert.Equal(value, Kind(kind).RoundTrip(value));
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public void Equality_ComparesIndexAndGeneration(string kind)
    {
        HandleKind k = Kind(kind);
        Handle value = new(3, 7);

        HandleComparison same = k.Compare(value, new Handle(3, 7));
        HandleComparison otherIndex = k.Compare(value, new Handle(4, 7));
        HandleComparison otherGeneration = k.Compare(value, new Handle(3, 8));
        HandleComparison nullHandle = k.Compare(value, Handle.Null);

        Assert.Equal(new HandleComparison(true, true, true, false, true), same);
        Assert.Equal((false, false, false, true), Channels(otherIndex));
        Assert.Equal((false, false, false, true), Channels(otherGeneration));
        Assert.Equal((false, false, false, true), Channels(nullHandle));
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public void Equals_NullOrOtherType_IsFalse(string kind)
    {
        HandleKind k = Kind(kind);
        Handle value = new(3, 7);

        Assert.False(k.EqualsObject(value, null));
        Assert.False(k.EqualsObject(value, value));
        Assert.False(k.EqualsObject(value, "Buffer(3:7)"));
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public void Equals_SameHandleOfAnotherKind_IsFalse(string kind)
    {
        HandleKind k = Kind(kind);
        Handle value = new(3, 7);

        Assert.All(_kinds.Where(other => other != k), other => Assert.False(k.EqualsObject(value, other.Box(value))));
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public void Type_HasNoConversionToAnotherKind(string kind)
    {
        Type type = Kind(kind).Type;
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        Assert.DoesNotContain(type.GetMethods(All), m => m.Name is "op_Implicit" or "op_Explicit");
        Assert.Equal([typeof(IEquatable<>).MakeGenericType(type)], type.GetInterfaces());
    }

    [Theory]
    [MemberData(nameof(KindNames))]
    public void Type_IsReadOnlyStructWithTheSharedPublicMembers(string kind)
    {
        Type type = Kind(kind).Type;
        string self = type.FullName!;
        string[] expected =
        [
            "Boolean Equals(System.Object)",
            $"Boolean Equals({self})",
            "Boolean get_IsNull()",
            "Boolean IsNull",
            $"Boolean op_Equality({self}, {self})",
            $"Boolean op_Inequality({self}, {self})",
            "Int32 GetHashCode()",
            "System.String ToString()",
        ];

        string[] members =
        [
            .. type.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(m => m.ToString()!)
                .Order(StringComparer.Ordinal),
        ];

        Assert.True(type.IsValueType);
        Assert.True(type.IsDefined(typeof(IsReadOnlyAttribute), inherit: false));
        Assert.Equal(expected.Order(StringComparer.Ordinal), members);
    }

    [Theory]
    [MemberData(nameof(ToStringCases))]
    public void ToString_Formats(string kind, uint index, uint generation, string expected)
    {
        HandleKind k = Kind(kind);

        string actual = generation == 0 ? k.DefaultToString() : k.Format(new Handle(index, generation));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Equality_IsReflexiveSymmetricTransitive()
    {
        Gen.Select(Gen.Int[0, _kinds.Length - 1], _handles, _handles, _handles).Sample((kindIndex, a, b, c) =>
        {
            HandleKind k = _kinds[kindIndex];
            bool reflexive = k.Compare(a, a) == new HandleComparison(true, true, true, false, true);
            bool symmetric = k.Compare(a, b) == k.Compare(b, a);
            bool transitive = !(k.Compare(a, b).TypedEquals && k.Compare(b, c).TypedEquals) || k.Compare(a, c).TypedEquals;
            return reflexive && symmetric && transitive;
        });
    }

    [Fact]
    public void Equality_AgreesWithCoreHandleOnEveryChannel()
    {
        Gen.Select(Gen.Int[0, _kinds.Length - 1], _handles, _handles).Sample((kindIndex, a, b) =>
        {
            HandleComparison c = _kinds[kindIndex].Compare(a, b);
            bool equal = a == b;
            return Channels(c) == (equal, equal, equal, !equal) && (!equal || c.SameHashCode);
        });
    }

    private static HandleKind Kind(string name) => _kinds.Single(k => k.Name == name);

    private static (bool TypedEquals, bool ObjectEquals, bool EqualityOperator, bool InequalityOperator) Channels(HandleComparison c) =>
        (c.TypedEquals, c.ObjectEquals, c.EqualityOperator, c.InequalityOperator);
}
