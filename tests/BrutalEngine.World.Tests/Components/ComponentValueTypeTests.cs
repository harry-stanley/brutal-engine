using System;
using System.Reflection;
using Xunit;

namespace BrutalEngine.World.Tests.Components;

public sealed class ComponentValueTypeTests
{
    // FNV-1a 64 of "MyGame.Velocity" (StableComponentIdTests pins it); a constant, so these tests don't depend on the hash.
    private static StableComponentId VelocityId => new(0x20E35CE39F30210A);

    private static ComponentInfo VelocityInfo => new()
    {
        StableId = VelocityId,
        SchemaVersion = 1,
        FullName = "MyGame.Velocity",
        IsTag = false,
    };

    public static TheoryData<string> InfoChanges => ["StableId", "SchemaVersion", "FullName", "FullNameCase", "IsTag"];

    public static TheoryData<string> TypeChanges => ["Id", "Info", "Size", "Alignment"];

    [Fact]
    public void StableComponentId_Default_IsZero()
    {
        Assert.Equal(0UL, default(StableComponentId).Value);
        Assert.Equal(new StableComponentId(0), default);
    }

    [Fact]
    public void StableComponentId_Equality_ComparesValue()
    {
        Assert.Equal(new StableComponentId(42), new StableComponentId(42));
        Assert.NotEqual(new StableComponentId(42), new StableComponentId(43));
        Assert.True(new StableComponentId(7) == new StableComponentId(7));
        Assert.Equal(new StableComponentId(42).GetHashCode(), new StableComponentId(42).GetHashCode());
    }

    [Fact]
    public void ComponentId_Default_HasIndexZero()
    {
        Assert.Equal(0, default(ComponentId).Index);
        Assert.Equal(new ComponentId(0), default);
    }

    [Fact]
    public void ComponentId_Equality_ComparesIndex()
    {
        Assert.Equal(new ComponentId(3), new ComponentId(3));
        Assert.NotEqual(new ComponentId(3), new ComponentId(4));
        Assert.True(new ComponentId(3) != new ComponentId(4));
        Assert.Equal(new ComponentId(3).GetHashCode(), new ComponentId(3).GetHashCode());
    }

    [Fact]
    public void ComponentId_OutsideAssembly_CantChooseIndex()
    {
        // Only registries issue ids: no public constructor takes an index, and Index has no setter or init accessor
        // (which a `with` expression could use).
        Type type = typeof(ComponentId);

        Assert.Empty(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.Null(type.GetProperty(nameof(ComponentId.Index))!.SetMethod);
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.Static), m => m.ReturnType == type && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(int));
    }

    [Fact]
    public void ComponentInfo_Default_HasNullName()
    {
        ComponentInfo info = default;

        Assert.Null(info.FullName);
        Assert.Equal(default, info.StableId);
        Assert.Equal(0u, info.SchemaVersion);
        Assert.False(info.IsTag);
    }

    [Fact]
    public void ComponentInfo_SameProperties_AreEqual()
    {
        ComponentInfo copy = new()
        {
            StableId = new StableComponentId(VelocityId.Value),
            SchemaVersion = 1,
            FullName = string.Concat("MyGame.", "Velocity"),
            IsTag = false,
        };

        Assert.Equal(VelocityInfo, copy);
        Assert.True(VelocityInfo == copy);
        Assert.Equal(VelocityInfo.GetHashCode(), copy.GetHashCode());
    }

    [Theory]
    [MemberData(nameof(InfoChanges))]
    public void ComponentInfo_AnyPropertyDiffers_AreNotEqual(string property)
    {
        ComponentInfo changed = property switch
        {
            "StableId" => VelocityInfo with { StableId = new StableComponentId(VelocityInfo.StableId.Value + 1) },
            "SchemaVersion" => VelocityInfo with { SchemaVersion = 2 },
            "FullName" => VelocityInfo with { FullName = "MyGame.Position" },
            "FullNameCase" => VelocityInfo with { FullName = "MyGame.velocity" },
            "IsTag" => VelocityInfo with { IsTag = true },
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, null),
        };

        Assert.NotEqual(VelocityInfo, changed);
        Assert.True(VelocityInfo != changed);
    }

    [Fact]
    public void ComponentType_Default_IsAllZero()
    {
        ComponentType type = default;

        Assert.Equal(default, type.Id);
        Assert.Equal(default, type.Info);
        Assert.Equal(0, type.Size);
        Assert.Equal(0, type.Alignment);
    }

    [Theory]
    [MemberData(nameof(TypeChanges))]
    public void ComponentType_Equality_ComparesEveryProperty(string property)
    {
        ComponentType type = new(new ComponentId(2), VelocityInfo, 8, 4);
        ComponentType changed = property switch
        {
            "Id" => type with { Id = new ComponentId(3) },
            "Info" => type with { Info = VelocityInfo with { SchemaVersion = 2 } },
            "Size" => type with { Size = 12 },
            "Alignment" => type with { Alignment = 8 },
            _ => throw new ArgumentOutOfRangeException(nameof(property), property, null),
        };

        Assert.Equal(new ComponentType(new ComponentId(2), VelocityInfo, 8, 4), type);
        Assert.NotEqual(type, changed);
    }

    [Fact]
    public void IComponent_Info_ReadThroughTypeParameter()
    {
        ComponentInfo info = InfoOf<Marker>();

        Assert.Equal(Marker.Info, info);
        Assert.True(info.IsTag);
    }

    private static ComponentInfo InfoOf<T>()
        where T : unmanaged, IComponent => T.Info;

    // Only read back here, never registered, so its stable id needn't be the hash of its name.
    private struct Marker : IComponent
    {
        public static ComponentInfo Info { get; } = new()
        {
            StableId = new StableComponentId(0x1234),
            SchemaVersion = 1,
            FullName = "MyGame.Marker",
            IsTag = true,
        };
    }
}
