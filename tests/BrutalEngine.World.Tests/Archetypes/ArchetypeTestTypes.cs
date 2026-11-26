using System.Globalization;
using BrutalEngine.Core.Memory;
using BrutalEngine.World.Internal;

namespace BrutalEngine.World.Tests.Archetypes;

/// <summary>
/// Shared helpers for the archetype tests: a fresh tracking allocator, made-up component types registered through the
/// registry's internal seam, and component id arrays. The signature tests need none of them; the archetype set, edge
/// and row tests do.
/// </summary>
internal static class ArchetypeTestTypes
{
    /// <summary>A fresh tracking allocator per test, so each test's leak check sees only its own blocks.</summary>
    public static TrackingAllocator NewTracking() => new(new HeapAllocator(), "Archetypes");

    /// <summary>
    /// Registers <paramref name="count"/> made-up data components of <paramref name="size"/> bytes through the
    /// registry's internal seam, named after their ids, and returns the id of the first.
    /// </summary>
    public static ComponentId Forge(EcsWorld world, int count, int size = 4, int alignment = 4)
    {
        ComponentRegistry registry = world.Components;
        int first = registry.Count;
        for (int i = 0; i < count; i++)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"Tests.Archetype.C{registry.Count}");
            ComponentInfo info = new()
            {
                StableId = StableComponentId.FromFullName(name),
                SchemaVersion = 1,
                FullName = name,
                IsTag = false,
            };
            _ = registry.Add(null, ComponentOrdinal.Reserve(), in info, size, alignment);
        }

        return new ComponentId(first);
    }

    /// <summary>The ids with the given indices.</summary>
    public static ComponentId[] Ids(params int[] indices)
    {
        var ids = new ComponentId[indices.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = new ComponentId(indices[i]);
        }

        return ids;
    }
}
