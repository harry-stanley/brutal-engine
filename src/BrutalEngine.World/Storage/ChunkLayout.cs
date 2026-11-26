using System;
using System.Numerics;

namespace BrutalEngine.World.Storage;

/// <summary>
/// Where an archetype's data sits in each of its chunks: the row capacity and one column per non-tag component
/// (docs/design/world.md, section 4, "Chunk layout"). Computed once per archetype by <see cref="Compute"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Layout.</b> A chunk holds <see cref="Capacity"/> rows. The rows' entity ids come first, at offset <c>0</c>, as
/// <see cref="Capacity"/> values of 8 bytes. Each column follows in the order of the sorted component types, at the
/// first offset after the previous region that is a multiple of <c>max(16, alignment)</c>, and holds
/// <see cref="Capacity"/> values of the component's size. Tags have no column. <see cref="Capacity"/> is the largest
/// row count whose layout ends within the chunk.
/// </para>
/// <para>
/// <b>Row width.</b> An archetype must fit at least <see cref="MinCapacity"/> rows in a chunk, alignment padding
/// included (section 3); <see cref="Compute"/> rejects a wider one.
/// </para>
/// <para>
/// Immutable after construction and safe to read from any thread. <see cref="Compute"/> allocates the column arrays
/// once per archetype (setup work); no other member allocates.
/// </para>
/// </remarks>
internal sealed class ChunkLayout
{
    /// <summary>The fewest rows a chunk must hold for an archetype to exist (docs/design/world.md, section 3).</summary>
    public const int MinCapacity = 16;

    /// <summary>The size of one entity id in a chunk, in bytes.</summary>
    public const int EntityIdBytes = 8;

    /// <summary>The smallest column alignment, in bytes, whatever the component's own alignment.</summary>
    public const int MinColumnAlignment = 16;

    private readonly ComponentType[] _columns;
    private readonly int[] _offsets;
    private readonly int[] _columnOfType;

    private ChunkLayout(int chunkBytes, int capacity, ComponentType[] columns, int[] offsets, int[] columnOfType)
    {
        ChunkBytes = chunkBytes;
        Capacity = capacity;
        _columns = columns;
        _offsets = offsets;
        _columnOfType = columnOfType;
    }

    /// <summary>The size of the chunk the layout was computed for, in bytes.</summary>
    public int ChunkBytes { get; }

    /// <summary>The number of rows a chunk holds: at least <see cref="MinCapacity"/>.</summary>
    public int Capacity { get; }

    /// <summary>The number of columns: the archetype's non-tag components.</summary>
    public int ColumnCount => _columns.Length;

    /// <summary>The component of each column, in column order (the sorted order, tags left out).</summary>
    public ReadOnlySpan<ComponentType> Columns => _columns;

    /// <summary>The offset of each column from the start of the chunk, in bytes, in column order.</summary>
    public ReadOnlySpan<int> ColumnOffsets => _offsets;

    /// <summary>The offset one past the last byte the layout uses: the end of the last column, or of the entity ids.</summary>
    public int UsedBytes => _columns.Length == 0
        ? Capacity * EntityIdBytes
        : _offsets[^1] + (Capacity * _columns[^1].Size);

    /// <summary>Returns the column of the component at <paramref name="typeIndex"/> in the sorted types, or -1 for a tag.</summary>
    /// <param name="typeIndex">The component's index in the span given to <see cref="Compute"/>.</param>
    /// <returns>The column index, or <c>-1</c> if the component is a tag.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="typeIndex"/> isn't an index of the sorted types.</exception>
    public int ColumnOf(int typeIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(typeIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(typeIndex, _columnOfType.Length);
        return _columnOfType[typeIndex];
    }

    /// <summary>Computes the layout of an archetype's chunks.</summary>
    /// <param name="sortedTypes">
    /// The archetype's components, in strictly ascending <see cref="ComponentId.Index"/> order, as a registry
    /// describes them: sizes from 1 to 1,024 bytes and power-of-two alignments up to 64.
    /// </param>
    /// <param name="chunkBytes">The chunk size in bytes, from 1 to 1 MiB (the world passes <c>WorldOptions.ChunkBytes</c>).</param>
    /// <returns>The layout. Pure: the same arguments always give the same layout.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="chunkBytes"/> is outside its range.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="sortedTypes"/> isn't strictly ascending, or a component's size or alignment is outside the
    /// registry's limits.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The row is too wide: fewer than <see cref="MinCapacity"/> rows fit in a chunk, alignment padding included. The
    /// message names the components, the row's size and the limit.
    /// </exception>
    public static ChunkLayout Compute(ReadOnlySpan<ComponentType> sortedTypes, int chunkBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(chunkBytes, WorldOptions.LargestChunkBytes);

        int columnCount = 0;
        long rowBytes = EntityIdBytes;
        for (int i = 0; i < sortedTypes.Length; i++)
        {
            ComponentType type = sortedTypes[i];
            if (i > 0 && type.Id.Index <= sortedTypes[i - 1].Id.Index)
            {
                throw new ArgumentException(
                    $"Component types must be in strictly ascending id order; index {i} has id {type.Id.Index} after {sortedTypes[i - 1].Id.Index}.",
                    nameof(sortedTypes));
            }

            if (type.Size is < 1 or > ComponentRegistry.MaxComponentSize
                || !BitOperations.IsPow2(type.Alignment) || type.Alignment > ComponentRegistry.MaxComponentAlignment)
            {
                throw new ArgumentException(
                    $"Component {type.Info.FullName} has size {type.Size} and alignment {type.Alignment}, outside the registry's limits (size 1 to {ComponentRegistry.MaxComponentSize}, a power-of-two alignment up to {ComponentRegistry.MaxComponentAlignment}).",
                    nameof(sortedTypes));
            }

            if (!type.Info.IsTag)
            {
                columnCount++;
                rowBytes += type.Size;
            }
        }

        // Padding only adds bytes, so chunkBytes / rowBytes rows is an upper bound, and the layout's end grows with the
        // row count, so the first count down from there that fits is the largest. Each column adds under 64 bytes of
        // padding, so the loop runs at most columnCount * 64 / rowBytes + 1 times.
        int capacity = (int)(chunkBytes / rowBytes);
        while (capacity > 0 && LayoutEnd(sortedTypes, capacity) > chunkBytes)
        {
            capacity--;
        }

        if (capacity < MinCapacity)
        {
            throw TooWide(sortedTypes, rowBytes, chunkBytes);
        }

        var columns = new ComponentType[columnCount];
        int[] offsets = new int[columnCount];
        int[] columnOfType = new int[sortedTypes.Length];
        long offset = (long)capacity * EntityIdBytes;
        int column = 0;
        for (int i = 0; i < sortedTypes.Length; i++)
        {
            ComponentType type = sortedTypes[i];
            if (type.Info.IsTag)
            {
                columnOfType[i] = -1;
                continue;
            }

            offset = AlignUp(offset, Math.Max(MinColumnAlignment, type.Alignment));
            columns[column] = type;
            offsets[column] = (int)offset;
            columnOfType[i] = column;
            offset += (long)capacity * type.Size;
            column++;
        }

        return new ChunkLayout(chunkBytes, capacity, columns, offsets, columnOfType);
    }

    // The end offset of a layout of `capacity` rows. Long, so an over-estimate can't overflow: at most 2^17 rows
    // (1 MiB of 8-byte ids) of at most 2^10 bytes per column, so overflowing would take 2^36 columns.
    private static long LayoutEnd(ReadOnlySpan<ComponentType> sortedTypes, int capacity)
    {
        long offset = (long)capacity * EntityIdBytes;
        foreach (ComponentType type in sortedTypes)
        {
            if (!type.Info.IsTag)
            {
                offset = AlignUp(offset, Math.Max(MinColumnAlignment, type.Alignment)) + ((long)capacity * type.Size);
            }
        }

        return offset;
    }

    private static long AlignUp(long offset, int alignment) => (offset + alignment - 1) & ~(long)(alignment - 1);

    private static InvalidOperationException TooWide(ReadOnlySpan<ComponentType> sortedTypes, long rowBytes, int chunkBytes)
    {
        string[] names = new string[sortedTypes.Length];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = sortedTypes[i].Info.FullName;
        }

        return new InvalidOperationException(
            $"An archetype with components [{string.Join(", ", names)}] has a {rowBytes}-byte row (an {EntityIdBytes}-byte entity id plus its columns), "
            + $"and {MinCapacity} rows take {LayoutEnd(sortedTypes, MinCapacity)} bytes with column alignment, more than the {chunkBytes}-byte chunk. "
            + $"An archetype must fit at least {MinCapacity} rows in a chunk; keep large data outside the world behind a handle.");
    }
}
