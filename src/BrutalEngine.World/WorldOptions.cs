using System;
using BrutalEngine.Core.Memory;

namespace BrutalEngine.World;

/// <summary>Settings for an <see cref="EcsWorld"/>. Validated by the world's constructor.</summary>
/// <remarks>
/// An immutable record: the world copies what it needs when it is constructed, so a later <c>with</c> copy has no
/// effect on it. Safe to share between threads. Creating one allocates; reading it doesn't.
/// </remarks>
public sealed record WorldOptions
{
    internal const int SmallestChunkBytes = 16 * 1024;
    internal const int LargestChunkBytes = 1024 * 1024;
    internal const int ComponentTypeGranularity = 64;
    internal const int LargestMaxComponentTypes = 4096;

    /// <summary>
    /// The allocator every unmanaged block of the world comes from. Default <see langword="null"/>: the world creates
    /// a <see cref="HeapAllocator"/> of its own.
    /// </summary>
    /// <remarks>
    /// A given allocator is not owned: the world never disposes it, and the caller keeps it alive until the world is
    /// disposed. Tests pass a <see cref="TrackingAllocator"/> and call <see cref="TrackingAllocator.ThrowIfLeaks"/>
    /// after <see cref="EcsWorld.Dispose"/>.
    /// </remarks>
    public INativeAllocator? Allocator { get; init; }

    /// <summary>
    /// Size in bytes of every storage chunk. Default 64 KiB; valid values are powers of two from 16 KiB to 1 MiB.
    /// </summary>
    public int ChunkBytes { get; init; } = 64 * 1024;

    /// <summary>Entity slots reserved up front. Default 1024; valid from 1 to <see cref="MaxEntities"/>.</summary>
    public int InitialEntityCapacity { get; init; } = 1024;

    /// <summary>
    /// Maximum number of entity slots: live, pending and retired together. Default 2^24; valid from 1 to
    /// <see cref="int.MaxValue"/>.
    /// </summary>
    public int MaxEntities { get; init; } = 1 << 24;

    /// <summary>
    /// Maximum number of component types the world can register, which is the width of its archetype masks.
    /// Default 256; valid values are multiples of 64 from 64 to 4096.
    /// </summary>
    public int MaxComponentTypes { get; init; } = 256;

    /// <summary>Maximum depth of the parent-child hierarchy. Default 64; valid from 1.</summary>
    public int MaxHierarchyDepth { get; init; } = 64;

    /// <summary>
    /// Source of timestamps for per-system timings only; nothing else in the world reads it. Default
    /// <see cref="TimeProvider.System"/>. Must not be <see langword="null"/>.
    /// </summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    /// The world's name, for diagnostics and leak reports. Default <c>"World"</c>. Must not be
    /// <see langword="null"/> or empty.
    /// </summary>
    public string Name { get; init; } = "World";
}
