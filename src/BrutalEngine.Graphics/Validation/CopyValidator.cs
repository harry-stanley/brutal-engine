using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace BrutalEngine.Graphics.Validation;

/// <summary>
/// The front end's pure checks of copies and writes against the descriptions of the resources
/// they touch: the copy rules of docs/design/graphics.md section 6, which the device runs before it records a copy or
/// accepts a write.
/// </summary>
/// <remarks>
/// <para>
/// A violation throws <see cref="ArgumentException"/>, or <see cref="ArgumentOutOfRangeException"/> for a value out of
/// its range (including a flag outside its enum's members). Each exception's <see cref="ArgumentException.ParamName"/>
/// is the device method's parameter that holds the value (<c>copy</c>, <c>buffer</c>, <c>offset</c> or <c>data</c>), and its message names the type or method, the field, the value, any computed
/// bound, and the debug name of the buffer the field refers to.
/// </para>
/// <para>
/// The descriptions are inputs: the device resolves the handles (and throws for stale ones) before calling, so each
/// check depends only on its arguments. Range checks never form <c>offset + size</c>: they compare the size with
/// <c>Size - offset</c> once the offset is known to lie in the buffer, which can't overflow, so a huge offset or size is
/// rejected instead of wrapping. Stateless and safe to call from any thread. A valid copy or write allocates nothing; only the failure paths build messages.
/// </para>
/// </remarks>
internal static class CopyValidator
{
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

    [DoesNotReturn]
    private static void ThrowOutOfRange<T>(string paramName, string type, string field, string requirement, T actual, string? debugName) =>
        throw new ArgumentOutOfRangeException(paramName, Message(type, field, requirement, actual, debugName));

    [DoesNotReturn]
    private static void ThrowInvalid(string paramName, string type, string field, string requirement, string? debugName) =>
        throw new ArgumentException(
            string.Create(CultureInfo.InvariantCulture, $"{type}.{field} {requirement} ({Describe(debugName)})."),
            paramName);

    private static string Message<T>(string type, string field, string requirement, T actual, string? debugName) =>
        string.Create(CultureInfo.InvariantCulture, $"{type}.{field} {requirement}, but is {actual} ({Describe(debugName)}).");

    private static string Describe(string? debugName) => debugName is null ? "no debug name" : $"debug name '{debugName}'";
}
