using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace BrutalEngine.World.Tests.Entities;

/// <summary>The component types the entity tests spawn with, and a byte-wise value comparison.</summary>
internal static class EntityComponents
{
    /// <summary>The info a hand-written component gives for its full name.</summary>
    public static ComponentInfo InfoFor(string fullName, bool isTag = false) => new()
    {
        StableId = StableComponentId.FromFullName(fullName),
        SchemaVersion = 1,
        FullName = fullName,
        IsTag = isTag,
    };

    /// <summary>Whether two values have the same bytes: component equality without relying on the types' own.</summary>
    public static bool Same<T>(in T left, in T right)
        where T : unmanaged =>
        MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in left)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in right)));
}

/// <summary>An 8-byte data component.</summary>
internal struct Position(float x, float y) : IComponent
{
    public float X = x;
    public float Y = y;

    public static ComponentInfo Info { get; } = EntityComponents.InfoFor(typeof(Position).FullName!);
}

/// <summary>A 4-byte data component.</summary>
internal struct Health(int value) : IComponent
{
    public int Value = value;

    public static ComponentInfo Info { get; } = EntityComponents.InfoFor(typeof(Health).FullName!);
}

/// <summary>A 256-byte data component, so its archetypes fill a 16 KiB chunk with a few dozen rows.</summary>
internal struct Payload : IComponent
{
    public Longs32 Values;

    public Payload(long seed)
    {
        for (int i = 0; i < 32; i++)
        {
            Values[i] = seed + i;
        }
    }

    public static ComponentInfo Info { get; } = EntityComponents.InfoFor(typeof(Payload).FullName!);
}

/// <summary>A 1,024-byte data component: alone, its row is too wide for a 16 KiB chunk.</summary>
internal struct Huge : IComponent
{
    public Longs128 Values;

    public Huge(long first) => Values[0] = first;

    public static ComponentInfo Info { get; } = EntityComponents.InfoFor(typeof(Huge).FullName!);
}

/// <summary>A tag: no data, only a mask bit.</summary>
internal struct Marker : IComponent
{
    public static ComponentInfo Info { get; } = EntityComponents.InfoFor(typeof(Marker).FullName!, isTag: true);
}

/// <summary>A tag no test registers.</summary>
internal struct UnusedTag : IComponent
{
    public static ComponentInfo Info { get; } = EntityComponents.InfoFor(typeof(UnusedTag).FullName!, isTag: true);
}

/// <summary>A data component no test registers.</summary>
internal struct Unused(int value) : IComponent
{
    public int Value = value;

    public static ComponentInfo Info { get; } = EntityComponents.InfoFor(typeof(Unused).FullName!);
}

/// <summary>32 longs inline.</summary>
[InlineArray(32)]
internal struct Longs32
{
    private long _element;
}

/// <summary>128 longs inline.</summary>
[InlineArray(128)]
internal struct Longs128
{
    private long _element;
}
