using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace BrutalEngine.Graphics.Validation;

/// <summary>
/// The front end's pure checks of copies, writes and transient allocations against the descriptions of the resources
/// they touch: the copy rules of docs/design/graphics.md section 6, which the device runs before it records a copy or
/// accepts a write.
/// </summary>
/// <remarks>
/// <para>
/// A violation throws <see cref="ArgumentException"/>, or <see cref="ArgumentOutOfRangeException"/> for a value out of
/// its range (including a flag outside its enum's members). Each exception's <see cref="ArgumentException.ParamName"/>
/// is the device method's parameter that holds the value (<c>copy</c>, <c>buffer</c>, <c>offset</c>, <c>data</c>,
/// <c>byteCount</c> or <c>usage</c>), and its message names the type or method, the field, the value, any computed
/// bound, and the debug name of the buffer or texture the field refers to.
/// </para>
/// <para>
/// The descriptions are inputs: the device resolves the handles (and throws for stale ones) before calling, so each
/// check depends only on its arguments. Range checks never form <c>offset + size</c>: they compare the size with
/// <c>Size - offset</c> once the offset is known to lie in the buffer, which can't overflow, so a huge offset or size is
/// rejected instead of wrapping. Stateless and safe to call from any thread. A valid copy, write or allocation allocates
/// nothing; only the failure paths build messages.
/// </para>
/// </remarks>
internal static class CopyValidator
{
    private const TransientUsage AllTransientUsages = TransientUsage.Vertex | TransientUsage.Index | TransientUsage.Uniform
        | TransientUsage.Storage | TransientUsage.Indirect;

    // BufferCopy offsets and sizes are multiples of 4 (WebGPU's rule; Vulkan's vkCmdCopyBuffer has none).
    private const long BufferCopyAlignment = 4;

    /// <summary>Checks a buffer-to-buffer copy against both buffers.</summary>
    /// <param name="copy">The copy to check.</param>
    /// <param name="source">The description of the buffer <see cref="BufferCopy.Source"/> refers to; already valid.</param>
    /// <param name="destination">
    /// The description of the buffer <see cref="BufferCopy.Destination"/> refers to; already valid, and the same as
    /// <paramref name="source"/> when both handles are one buffer.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The source lacks <see cref="BufferUsage.CopySource"/> or the destination <see cref="BufferUsage.CopyDestination"/>;
    /// the size or an offset isn't a multiple of 4; or the source and destination are the same buffer and the ranges
    /// overlap.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The size isn't positive; an offset is negative or past its buffer's end; or a range reaches past its buffer's end.
    /// </exception>
    public static void Validate(in BufferCopy copy, in BufferDescription source, in BufferDescription destination)
    {
        const string Type = nameof(BufferCopy);
        CheckUsage(nameof(copy), Type, nameof(BufferCopy.Source), source, BufferUsage.CopySource);
        CheckUsage(nameof(copy), Type, nameof(BufferCopy.Destination), destination, BufferUsage.CopyDestination);
        if (copy.Size <= 0)
        {
            ThrowOutOfRange(nameof(copy), Type, nameof(BufferCopy.Size), "must be greater than 0", copy.Size, source.DebugName);
        }

        CheckAligned(nameof(copy), Type, nameof(BufferCopy.Size), copy.Size, source.DebugName);
        CheckRange(nameof(copy), Type, nameof(BufferCopy.SourceOffset), nameof(copy), nameof(BufferCopy.Size), copy.SourceOffset, copy.Size, source, "source");
        CheckAligned(nameof(copy), Type, nameof(BufferCopy.SourceOffset), copy.SourceOffset, source.DebugName);
        CheckRange(nameof(copy), Type, nameof(BufferCopy.DestinationOffset), nameof(copy), nameof(BufferCopy.Size), copy.DestinationOffset, copy.Size, destination, "destination");
        CheckAligned(nameof(copy), Type, nameof(BufferCopy.DestinationOffset), copy.DestinationOffset, destination.DebugName);

        // Both ranges lie inside the one buffer here, so neither end can overflow.
        if (copy.Source == copy.Destination
            && copy.SourceOffset < copy.DestinationOffset + copy.Size
            && copy.DestinationOffset < copy.SourceOffset + copy.Size)
        {
            ThrowInvalid(
                nameof(copy),
                Type,
                nameof(BufferCopy.DestinationOffset),
                string.Create(CultureInfo.InvariantCulture, $"must not make the range overlap the source range [{copy.SourceOffset}, {copy.SourceOffset + copy.Size}) of the same buffer, but is {copy.DestinationOffset}"),
                source.DebugName);
        }
    }

    /// <summary>Checks a buffer write's range against the buffer it writes.</summary>
    /// <param name="target">The description of the buffer written; already valid.</param>
    /// <param name="offset">The byte offset in the buffer that the data goes to. Needs no alignment.</param>
    /// <param name="dataLength">The length of the data, in bytes.</param>
    /// <exception cref="ArgumentException">The buffer lacks <see cref="BufferUsage.CopyDestination"/> (parameter <c>buffer</c>).</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The offset is negative or past the buffer's end (parameter <c>offset</c>); or the data is empty or reaches past
    /// the buffer's end (parameter <c>data</c>).
    /// </exception>
    public static void ValidateWriteBuffer(in BufferDescription target, long offset, int dataLength)
    {
        const string Type = "WriteBuffer";
        const string Length = "data.Length";
        CheckUsage("buffer", Type, "buffer", target, BufferUsage.CopyDestination);
        if (dataLength <= 0)
        {
            ThrowOutOfRange("data", Type, Length, "must be greater than 0", dataLength, target.DebugName);
        }

        CheckRange(nameof(offset), Type, nameof(offset), "data", Length, offset, dataLength, target, "target");
    }

    /// <summary>Checks a texture-to-buffer copy against the texture and the buffer.</summary>
    /// <param name="copy">The copy to check.</param>
    /// <param name="texture">The description of the texture copied from; already valid.</param>
    /// <param name="buffer">The description of the buffer copied to; already valid.</param>
    /// <exception cref="ArgumentException">
    /// The texture lacks <see cref="TextureUsage.CopySource"/> or the buffer <see cref="BufferUsage.CopyDestination"/>;
    /// the texture has more than one sample; the aspect doesn't match the format; <see cref="TextureBufferCopy.BytesPerRow"/>
    /// isn't a multiple of <see cref="TextureBufferCopy.RowPitchAlignment"/>; or <see cref="TextureBufferCopy.BufferOffset"/>
    /// isn't a multiple of the texel size and of 4.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The aspect is outside its enum; the mip level, layer or region lies outside the texture;
    /// <see cref="TextureBufferCopy.BytesPerRow"/> is below the region's row size; or the buffer offset is negative or
    /// leaves too few bytes for the rows.
    /// </exception>
    public static void ValidateCopyToBuffer(in TextureBufferCopy copy, in TextureDescription texture, in BufferDescription buffer)
    {
        CheckTextureUsage(nameof(copy), nameof(TextureBufferCopy), nameof(TextureBufferCopy.Texture), texture, TextureUsage.CopySource);
        CheckUsage(nameof(copy), nameof(TextureBufferCopy), nameof(TextureBufferCopy.Buffer), buffer, BufferUsage.CopyDestination);
        CheckTextureBufferCopy(copy, texture, buffer);
    }

    /// <summary>Checks a buffer-to-texture copy against the buffer and the texture.</summary>
    /// <param name="copy">The copy to check.</param>
    /// <param name="buffer">The description of the buffer copied from; already valid.</param>
    /// <param name="texture">The description of the texture copied to; already valid.</param>
    /// <exception cref="ArgumentException">
    /// The buffer lacks <see cref="BufferUsage.CopySource"/> or the texture <see cref="TextureUsage.CopyDestination"/>;
    /// the texture has more than one sample; the aspect doesn't match the format, or is <see cref="TextureAspect.Depth"/>
    /// (depth is copied to buffers, never from them); <see cref="TextureBufferCopy.BytesPerRow"/> isn't a multiple of
    /// <see cref="TextureBufferCopy.RowPitchAlignment"/>; or <see cref="TextureBufferCopy.BufferOffset"/> isn't a
    /// multiple of the texel size and of 4.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The aspect is outside its enum; the mip level, layer or region lies outside the texture;
    /// <see cref="TextureBufferCopy.BytesPerRow"/> is below the region's row size; or the buffer offset is negative or
    /// leaves too few bytes for the rows.
    /// </exception>
    public static void ValidateCopyToTexture(in TextureBufferCopy copy, in BufferDescription buffer, in TextureDescription texture)
    {
        CheckUsage(nameof(copy), nameof(TextureBufferCopy), nameof(TextureBufferCopy.Buffer), buffer, BufferUsage.CopySource);
        CheckTextureUsage(nameof(copy), nameof(TextureBufferCopy), nameof(TextureBufferCopy.Texture), texture, TextureUsage.CopyDestination);
        CheckTextureBufferCopy(copy, texture, buffer);

        // A deliberate simplification (docs/design/graphics.md section 6): WebGPU forbids this for Depth32Float only.
        if (copy.Texture.Aspect == TextureAspect.Depth)
        {
            ThrowInvalid(nameof(copy), nameof(TextureBufferCopy), "Texture.Aspect", "must not be Depth: depth can be copied to buffers but never from them", texture.DebugName);
        }
    }

    /// <summary>Checks a transient allocation request's size and usage.</summary>
    /// <param name="byteCount">The bytes requested.</param>
    /// <param name="usage">What the allocation will be bound as.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="byteCount"/> isn't positive, or <paramref name="usage"/> has a flag that isn't a
    /// <see cref="TransientUsage"/> member.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="usage"/> has no flag.</exception>
    public static void ValidateTransientAllocation(long byteCount, TransientUsage usage)
    {
        const string Type = "AllocateTransient";
        if (byteCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(byteCount),
                string.Create(CultureInfo.InvariantCulture, $"{Type}.{nameof(byteCount)} must be greater than 0, but is {byteCount}."));
        }

        if ((usage & ~AllTransientUsages) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(usage),
                string.Create(CultureInfo.InvariantCulture, $"{Type}.{nameof(usage)} has flags that aren't TransientUsage members, but is {(long)usage}."));
        }

        if (usage == 0)
        {
            throw new ArgumentException($"{Type}.{nameof(usage)} must have at least one flag.", nameof(usage));
        }
    }

    private static void CheckUsage(string paramName, string type, string field, in BufferDescription buffer, BufferUsage required)
    {
        if ((buffer.Usage & required) == 0)
        {
            ThrowInvalid(
                paramName,
                type,
                field,
                string.Create(CultureInfo.InvariantCulture, $"must be a buffer with {required} usage, but has {buffer.Usage}"),
                buffer.DebugName);
        }
    }

    private static void CheckAligned(string paramName, string type, string field, long value, string? debugName)
    {
        if (value % BufferCopyAlignment != 0)
        {
            ThrowInvalid(
                paramName,
                type,
                field,
                string.Create(CultureInfo.InvariantCulture, $"must be a multiple of {BufferCopyAlignment}, but is {value}"),
                debugName);
        }
    }

    // A range [offset, offset + length) of a buffer, with length already positive: the offset from 0 to the buffer's
    // size, then the length at most the bytes after it. No sum is formed, so a huge offset or length can't wrap.
    private static void CheckRange(string offsetParam, string type, string offsetField, string lengthParam, string lengthField, long offset, long length, in BufferDescription buffer, string role)
    {
        if (offset < 0 || offset > buffer.Size)
        {
            ThrowOutOfRange(
                offsetParam,
                type,
                offsetField,
                string.Create(CultureInfo.InvariantCulture, $"must be from 0 to the {role} buffer's size ({buffer.Size})"),
                offset,
                buffer.DebugName);
        }

        // 0 <= offset <= Size, so Size - offset can't overflow.
        long available = buffer.Size - offset;
        if (length > available)
        {
            ThrowOutOfRange(
                lengthParam,
                type,
                lengthField,
                string.Create(CultureInfo.InvariantCulture, $"must be at most the {role} buffer's size minus {offsetField} ({available})"),
                length,
                buffer.DebugName);
        }
    }

    // The rules a TextureBufferCopy has in both directions, after the usages: the texture side, then the buffer rows.
    private static void CheckTextureBufferCopy(in TextureBufferCopy copy, in TextureDescription texture, in BufferDescription buffer)
    {
        const string Type = nameof(TextureBufferCopy);
        CheckSingleSample(nameof(copy), Type, nameof(TextureBufferCopy.Texture), texture);
        CheckLocation(nameof(copy), Type, nameof(TextureBufferCopy.Texture), copy.Texture, texture);
        CheckRegion(nameof(copy), Type, nameof(TextureBufferCopy.Region), copy.Region, copy.Texture.MipLevel, texture);

        int texelSize = TextureFormatInfo.TexelSize(texture.Format);

        // The region lies inside its mip level, so Width is at most int.MaxValue and this can't overflow a long.
        long rowBytes = (long)copy.Region.Width * texelSize;
        if (copy.BytesPerRow < rowBytes)
        {
            ThrowOutOfRange(
                nameof(copy),
                Type,
                nameof(TextureBufferCopy.BytesPerRow),
                string.Create(CultureInfo.InvariantCulture, $"must be at least Region.Width times the texel size ({rowBytes})"),
                copy.BytesPerRow,
                texture.DebugName);
        }

        if (copy.BytesPerRow % TextureBufferCopy.RowPitchAlignment != 0)
        {
            ThrowInvalid(
                nameof(copy),
                Type,
                nameof(TextureBufferCopy.BytesPerRow),
                string.Create(CultureInfo.InvariantCulture, $"must be a multiple of {TextureBufferCopy.RowPitchAlignment}, but is {copy.BytesPerRow}"),
                texture.DebugName);
        }

        if (copy.BufferOffset < 0)
        {
            ThrowOutOfRange(nameof(copy), Type, nameof(TextureBufferCopy.BufferOffset), "must be at least 0", copy.BufferOffset, buffer.DebugName);
        }

        // Texel sizes are powers of two, so a multiple of the larger of 4 and the texel size is a multiple of both.
        long offsetAlignment = Math.Max(BufferCopyAlignment, texelSize);
        if (copy.BufferOffset % offsetAlignment != 0)
        {
            ThrowInvalid(
                nameof(copy),
                Type,
                nameof(TextureBufferCopy.BufferOffset),
                string.Create(CultureInfo.InvariantCulture, $"must be a multiple of the texel size and of 4 ({offsetAlignment}), but is {copy.BufferOffset}"),
                buffer.DebugName);
        }

        // BytesPerRow and Height are positive ints, so the product is below 2^62 and the sum below 2^63. The offset is
        // non-negative and the size positive, so Size - BufferOffset can't overflow either: no sum with the offset is formed.
        long required = ((long)copy.BytesPerRow * (copy.Region.Height - 1)) + rowBytes;
        long available = buffer.Size - copy.BufferOffset;
        if (required > available)
        {
            ThrowOutOfRange(
                nameof(copy),
                Type,
                nameof(TextureBufferCopy.BufferOffset),
                string.Create(CultureInfo.InvariantCulture, $"must leave the {required} bytes the rows need (BytesPerRow * (Region.Height - 1) + Region.Width * texel size) before the buffer's end ({buffer.Size})"),
                copy.BufferOffset,
                buffer.DebugName);
        }
    }

    private static void CheckTextureUsage(string paramName, string type, string field, in TextureDescription texture, TextureUsage required)
    {
        if ((texture.Usage & required) == 0)
        {
            ThrowInvalid(
                paramName,
                type,
                field,
                string.Create(CultureInfo.InvariantCulture, $"must be a texture with {required} usage, but has {texture.Usage}"),
                texture.DebugName);
        }
    }

    // Vulkan copies only single-sampled images to and from buffers (VUID-vkCmdCopyBufferToImage-dstImage-07973).
    private static void CheckSingleSample(string paramName, string type, string field, in TextureDescription texture)
    {
        if (texture.SampleCount != 1)
        {
            ThrowInvalid(
                paramName,
                type,
                field,
                string.Create(CultureInfo.InvariantCulture, $"must be a texture with one sample, but has {texture.SampleCount}"),
                texture.DebugName);
        }
    }

    // A location's aspect, mip level and layer against its texture. The prefix names the location's field, joined to
    // the location's own field only on the throw path.
    private static void CheckLocation(string paramName, string type, string prefix, in TextureLocation location, in TextureDescription texture)
    {
        if ((uint)location.Aspect > (uint)TextureAspect.Depth)
        {
            ThrowOutOfRange(paramName, type, prefix, nameof(TextureLocation.Aspect), "isn't a TextureAspect member", (long)location.Aspect, texture.DebugName);
        }

        if (location.MipLevel < 0 || location.MipLevel >= texture.MipLevels)
        {
            ThrowOutOfRange(
                paramName,
                type,
                prefix,
                nameof(TextureLocation.MipLevel),
                string.Create(CultureInfo.InvariantCulture, $"must be from 0 to the texture's mip levels minus 1 ({texture.MipLevels - 1})"),
                location.MipLevel,
                texture.DebugName);
        }

        if (location.ArrayLayer < 0 || location.ArrayLayer >= texture.ArrayLayers)
        {
            ThrowOutOfRange(
                paramName,
                type,
                prefix,
                nameof(TextureLocation.ArrayLayer),
                string.Create(CultureInfo.InvariantCulture, $"must be from 0 to the texture's layers minus 1 ({texture.ArrayLayers - 1})"),
                location.ArrayLayer,
                texture.DebugName);
        }

        bool depth = TextureFormatInfo.IsDepth(texture.Format);
        if (depth != (location.Aspect == TextureAspect.Depth))
        {
            ThrowInvalid(
                paramName,
                type,
                prefix,
                nameof(TextureLocation.Aspect),
                string.Create(CultureInfo.InvariantCulture, $"must be {(depth ? TextureAspect.Depth : TextureAspect.Color)} for the texture's {texture.Format} format, but is {location.Aspect}"),
                texture.DebugName);
        }
    }

    // A region inside its mip level; the mip level is already in range.
    private static void CheckRegion(string paramName, string type, string prefix, in TextureRegion region, int mipLevel, in TextureDescription texture)
    {
        CheckSpan(paramName, type, prefix, nameof(TextureRegion.X), nameof(TextureRegion.Width), region.X, region.Width, MipSize(texture.Width, mipLevel), "width", texture.DebugName);
        CheckSpan(paramName, type, prefix, nameof(TextureRegion.Y), nameof(TextureRegion.Height), region.Y, region.Height, MipSize(texture.Height, mipLevel), "height", texture.DebugName);
    }

    // A span [origin, origin + size) of 0..extent on one axis: the origin inside, the size at least 1 and not past the end.
    private static void CheckSpan(string paramName, string type, string? prefix, string originField, string sizeField, int origin, int size, int extent, string axis, string? debugName)
    {
        if (origin < 0 || origin >= extent)
        {
            ThrowOutOfRange(
                paramName,
                type,
                prefix,
                originField,
                string.Create(CultureInfo.InvariantCulture, $"must be from 0 to the mip level's {axis} minus 1 ({extent - 1})"),
                origin,
                debugName);
        }

        // extent - origin can't overflow: extent is positive and origin is in [0, extent).
        if (size < 1 || size > extent - origin)
        {
            ThrowOutOfRange(
                paramName,
                type,
                prefix,
                sizeField,
                string.Create(CultureInfo.InvariantCulture, $"must be from 1 to the mip level's {axis} after {originField} ({extent - origin})"),
                size,
                debugName);
        }
    }

    // One side of a mip level: halved per level, never below 1. The level is in [0, MipLevels), at most 30.
    private static int MipSize(int size, int mipLevel) => Math.Max(1, size >> mipLevel);

    [DoesNotReturn]
    private static void ThrowOutOfRange<T>(string paramName, string type, string field, string requirement, T actual, string? debugName) =>
        throw new ArgumentOutOfRangeException(paramName, Message(type, field, requirement, actual, debugName));

    [DoesNotReturn]
    private static void ThrowInvalid(string paramName, string type, string field, string requirement, string? debugName) =>
        throw new ArgumentException(
            string.Create(CultureInfo.InvariantCulture, $"{type}.{field} {requirement} ({Describe(debugName)})."),
            paramName);

    [DoesNotReturn]
    private static void ThrowOutOfRange<T>(string paramName, string type, string? prefix, string field, string requirement, T actual, string? debugName) =>
        ThrowOutOfRange(paramName, type, Join(prefix, field), requirement, actual, debugName);

    [DoesNotReturn]
    private static void ThrowInvalid(string paramName, string type, string prefix, string field, string requirement, string? debugName) =>
        ThrowInvalid(paramName, type, Join(prefix, field), requirement, debugName);

    private static string Join(string? prefix, string field) => prefix is null ? field : string.Concat(prefix, ".", field);

    private static string Message<T>(string type, string field, string requirement, T actual, string? debugName) =>
        string.Create(CultureInfo.InvariantCulture, $"{type}.{field} {requirement}, but is {actual} ({Describe(debugName)}).");

    private static string Describe(string? debugName) => debugName is null ? "no debug name" : $"debug name '{debugName}'";
}
