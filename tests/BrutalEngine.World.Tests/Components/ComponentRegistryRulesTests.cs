using System;
using System.Runtime.CompilerServices;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Internal;
using Xunit;

namespace BrutalEngine.World.Tests.Components;

public sealed class ComponentRegistryRulesTests
{
    public static TheoryData<string> Rules =>
    [
        "size",
        "alignment",
        "schema-zero",
        "empty-name",
        "null-name",
        "stable-id",
        "tag-size",
        "collision",
        "schema",
        "tag-ness",
        "same-name",
    ];

    [Theory]
    [MemberData(nameof(Rules))]
    public void Rules_Throw(string rule)
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            ComponentRegistry registry = world.Components;
            Exception exception = rule switch
            {
                "size" => Assert.Throws<ArgumentException>(() => registry.Register<Oversized>()),

                // No ordinary .NET struct is aligned to more than 64 bytes, so this goes through the internal seam.
                "alignment" => Assert.Throws<ArgumentException>(
                    () => ComponentRegistry.Validate(typeof(Position), Position.Info, size: 128, alignment: 128)),
                "schema-zero" => Assert.Throws<ArgumentException>(() => registry.Register<SchemaZero>()),
                "empty-name" => Assert.Throws<ArgumentException>(() => registry.Register<EmptyName>()),
                "null-name" => Assert.Throws<ArgumentException>(() => registry.Register<NullName>()),
                "stable-id" => Assert.Throws<ArgumentException>(() => registry.Register<WrongStableId>()),
                "tag-size" => Assert.Throws<ArgumentException>(() => registry.Register<FatTag>()),

                // A real 64-bit FNV-1a collision between two valid names isn't practical to construct, so a forged
                // stable id goes in through the internal seam, past the info validation that would reject it.
                "collision" => Assert.Throws<InvalidOperationException>(() =>
                {
                    _ = registry.Register<Position>();
                    _ = registry.Add(null, ComponentOrdinal.Reserve(), Forged, 4, 4);
                }),
                "schema" => Assert.Throws<InvalidOperationException>(() =>
                {
                    _ = registry.Register<HealthV1>();
                    _ = registry.Register<HealthV2>();
                }),
                "tag-ness" => Assert.Throws<InvalidOperationException>(() =>
                {
                    _ = registry.Register<Marker>();
                    _ = registry.Register<MarkerData>();
                }),
                "same-name" => Assert.Throws<InvalidOperationException>(() =>
                {
                    _ = registry.Register<Alias>();
                    _ = registry.Register<OtherAlias>();
                }),
                _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
            };

            string[] expected = rule switch
            {
                "size" => [NameOf<Oversized>(), "1025 bytes", "1024 bytes"],
                "alignment" => [NameOf<Position>(), "128 bytes", "64 bytes"],
                "schema-zero" => [NameOf<SchemaZero>(), "ComponentInfo.SchemaVersion"],
                "empty-name" => [NameOf<EmptyName>(), "ComponentInfo.FullName"],
                "null-name" => [NameOf<NullName>(), "ComponentInfo.FullName"],
                "stable-id" => [NameOf<WrongStableId>(), "ComponentInfo.StableId"],
                "tag-size" => [NameOf<FatTag>(), "ComponentInfo.IsTag", "4 bytes"],
                "collision" => ["Tests.Forged", Position.Info.FullName],
                "schema" => ["Tests.Health", "schema version 2", "schema version 1"],
                "tag-ness" => ["Tests.Marker", "a data component", "already registered as a tag"],
                _ => ["Tests.Alias", "a different type"],
            };

            foreach (string text in expected)
            {
                Assert.Contains(text, exception.Message, StringComparison.Ordinal);
            }
        }

        tracking.ThrowIfLeaks();
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("tag-ness")]
    [InlineData("same-name")]
    public void SameNameConflict_MessageNamesNewType(string rule)
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            ComponentRegistry registry = world.Components;
            (Type first, Type second) = rule switch
            {
                "schema" => (typeof(HealthV1), typeof(HealthV2)),
                "tag-ness" => (typeof(Marker), typeof(MarkerData)),
                "same-name" => (typeof(Alias), typeof(OtherAlias)),
                _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
            };
            _ = Register(registry, first);

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => Register(registry, second));

            // Both types have the same full name, so only the CLR type and its assembly tell them apart.
            Assert.Contains(
                $"type {second.FullName} in assembly {second.Assembly.GetName().Name}",
                exception.Message,
                StringComparison.Ordinal);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void Rules_AtTheLimits_Register()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            ComponentRegistry registry = world.Components;

            ComponentType largest = registry.GetComponentType(registry.Register<MaxSized>());
            ComponentType tag = registry.GetComponentType(registry.Register<Marker>());

            Assert.Equal(1024, largest.Size);
            Assert.Equal((1, true), (tag.Size, tag.Info.IsTag));

            // The largest alignment allowed, through the seam, and a one-byte data component (not a tag).
            ComponentRegistry.Validate(typeof(Position), Position.Info, size: 64, alignment: 64);
            ComponentRegistry.Validate(typeof(Position), Position.Info, size: 1, alignment: 1);
        }

        tracking.ThrowIfLeaks();
    }

    [Fact]
    public void FailedRegistration_LeavesRegistryUnchanged()
    {
        TrackingAllocator tracking = NewTracking();
        using (EcsWorld world = NewWorld(tracking))
        {
            ComponentRegistry registry = world.Components;
            ComponentId[] registered =
            [
                registry.Register<Position>(),
                registry.Register<HealthV1>(),
                registry.Register<Marker>(),
                registry.Register<Alias>(),
            ];
            ComponentType[] before = Array.ConvertAll(registered, registry.GetComponentType);

            // Twice each: a failed type isn't half-registered, so it fails the same way again.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                _ = Assert.Throws<ArgumentException>(() => registry.Register<Oversized>());
                _ = Assert.Throws<ArgumentException>(() => registry.Register<SchemaZero>());
                _ = Assert.Throws<ArgumentException>(() => registry.Register<EmptyName>());
                _ = Assert.Throws<ArgumentException>(() => registry.Register<WrongStableId>());
                _ = Assert.Throws<ArgumentException>(() => registry.Register<FatTag>());
                _ = Assert.Throws<InvalidOperationException>(() => registry.Register<HealthV2>());
                _ = Assert.Throws<InvalidOperationException>(() => registry.Register<MarkerData>());
                _ = Assert.Throws<InvalidOperationException>(() => registry.Register<OtherAlias>());
                _ = Assert.Throws<InvalidOperationException>(() => registry.Add(null, ComponentOrdinal.Reserve(), Forged, 4, 4));
            }

            Assert.Equal(4, registry.Count);
            Assert.False(registry.TryGetId<Oversized>(out _));
            Assert.False(registry.TryGetId<SchemaZero>(out _));
            Assert.False(registry.TryGetId<EmptyName>(out _));
            Assert.False(registry.TryGetId<WrongStableId>(out _));
            Assert.False(registry.TryGetId<FatTag>(out _));
            Assert.False(registry.TryGetId<HealthV2>(out _));
            Assert.False(registry.TryGetId<MarkerData>(out _));
            Assert.False(registry.TryGetId<OtherAlias>(out _));
            Assert.False(registry.TryGetId(Oversized.Info.StableId, out _));
            Assert.False(registry.TryGetId(WrongStableId.Info.StableId, out _));

            // The conflicting stable ids still name the types registered first, unchanged.
            Assert.True(registry.TryGetId(Forged.StableId, out ComponentId position));
            Assert.Equal(registered[0], position);
            Assert.True(registry.TryGetId(HealthV2.Info.StableId, out ComponentId health));
            Assert.Equal(registered[1], health);
            Assert.True(registry.TryGetId(MarkerData.Info.StableId, out ComponentId marker));
            Assert.Equal(registered[2], marker);
            Assert.True(registry.TryGetId(OtherAlias.Info.StableId, out ComponentId alias));
            Assert.Equal(registered[3], alias);
            Assert.Equal(before, Array.ConvertAll(registered, registry.GetComponentType));

            // A later valid registration gets the next dense id.
            Assert.Equal(4, registry.Register<Velocity>().Index);
            Assert.Equal(5, registry.Count);
        }

        tracking.ThrowIfLeaks();
    }

    /// <summary>A data component's info whose stable id is Position's, under another name: a forged collision.</summary>
    private static ComponentInfo Forged => InfoFor("Tests.Forged") with { StableId = Position.Info.StableId };

    private static string NameOf<T>() => typeof(T).FullName!;

    private static ComponentId Register(ComponentRegistry registry, Type type) => type switch
    {
        _ when type == typeof(HealthV1) => registry.Register<HealthV1>(),
        _ when type == typeof(HealthV2) => registry.Register<HealthV2>(),
        _ when type == typeof(Marker) => registry.Register<Marker>(),
        _ when type == typeof(MarkerData) => registry.Register<MarkerData>(),
        _ when type == typeof(Alias) => registry.Register<Alias>(),
        _ when type == typeof(OtherAlias) => registry.Register<OtherAlias>(),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown fixture type."),
    };

    /// <summary>A valid info for a component named <paramref name="fullName"/>, as the generator would emit it.</summary>
    private static ComponentInfo InfoFor(string fullName, bool isTag = false, uint schemaVersion = 1) => new()
    {
        StableId = StableComponentId.FromFullName(fullName),
        SchemaVersion = schemaVersion,
        FullName = fullName,
        IsTag = isTag,
    };

    /// <summary>A fresh tracking allocator per test, so each leak check sees only its own blocks.</summary>
    private static TrackingAllocator NewTracking() => new(new HeapAllocator(), "ComponentRegistryRules");

    private static EcsWorld NewWorld(TrackingAllocator tracking) =>
        new(new WorldOptions { Allocator = tracking, InitialEntityCapacity = 4 });

    // Fixture components, hand-writing the Info the generator will emit; most are invalid on purpose. The primary
    // constructors only give the fields a writer; the registry needs nothing but the layout.
    private struct Position(float x, float y) : IComponent
    {
        public float X = x;
        public float Y = y;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Position).FullName!);
    }

    private struct Velocity(float x, float y) : IComponent
    {
        public float X = x;
        public float Y = y;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Velocity).FullName!);
    }

    /// <summary>1,025 bytes: one over the limit.</summary>
    private struct Oversized(Bytes1025 data) : IComponent
    {
        public Bytes1025 Data = data;
        public static ComponentInfo Info { get; } = InfoFor(typeof(Oversized).FullName!);
    }

    /// <summary>1,024 bytes: the limit.</summary>
    private struct MaxSized(Bytes1024 data) : IComponent
    {
        public Bytes1024 Data = data;
        public static ComponentInfo Info { get; } = InfoFor(typeof(MaxSized).FullName!);
    }

    private struct SchemaZero(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(typeof(SchemaZero).FullName!, schemaVersion: 0);
    }

    private struct EmptyName(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(string.Empty);
    }

    private struct NullName(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(string.Empty) with { FullName = null! };
    }

    /// <summary>Its stable id is another name's.</summary>
    private struct WrongStableId(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } =
            InfoFor(typeof(WrongStableId).FullName!) with { StableId = StableComponentId.FromFullName("Tests.Other") };
    }

    /// <summary>Claims to be a tag but has a field (4 bytes).</summary>
    private struct FatTag(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor(typeof(FatTag).FullName!, isTag: true);
    }

    private struct HealthV1(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor("Tests.Health");
    }

    /// <summary>The same name as <see cref="HealthV1"/>, with schema version 2.</summary>
    private struct HealthV2(int value, int max) : IComponent
    {
        public int Value = value;
        public int Max = max;
        public static ComponentInfo Info { get; } = InfoFor("Tests.Health", schemaVersion: 2);
    }

    private struct Marker : IComponent
    {
        public static ComponentInfo Info { get; } = InfoFor("Tests.Marker", isTag: true);
    }

    /// <summary>The same name as the tag <see cref="Marker"/>, as a data component.</summary>
    private struct MarkerData(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor("Tests.Marker");
    }

    private struct Alias(int value) : IComponent
    {
        public int Value = value;
        public static ComponentInfo Info { get; } = InfoFor("Tests.Alias");
    }

    /// <summary>A different type with <see cref="Alias"/>'s name, schema version and tag-ness.</summary>
    private struct OtherAlias(long value) : IComponent
    {
        public long Value = value;
        public static ComponentInfo Info { get; } = InfoFor("Tests.Alias");
    }

    [InlineArray(1025)]
    private struct Bytes1025
    {
        public byte Element;
    }

    [InlineArray(1024)]
    private struct Bytes1024
    {
        public byte Element;
    }
}
