namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// The payload of a <see cref="CommandKind.PushConstants"/> record: push-constant bytes written for the draws or
/// dispatches after it (docs/design/graphics.md, "The frame plan" and section 9).
/// </summary>
/// <remarks>
/// <para>
/// The record's trailing part is the <see cref="Size"/> bytes, copied from the encoder's value as they were in
/// memory. The front end checked that the range lies inside the pipeline layout's push-constant range before it
/// encoded the record.
/// </para>
/// <para>
/// An unmanaged value, so the command stream holds it as raw bytes and a reader returns it by reference into the
/// stream. Immutable and safe to copy between threads. No member allocates.
/// </para>
/// </remarks>
public readonly struct PushConstantsCommand
{
    /// <summary>The shader stages that see the bytes: the pipeline layout's push-constant stages.</summary>
    public ShaderStages Stages { get; init; }

    /// <summary>The byte offset of the bytes in the push-constant range: at least 0.</summary>
    public int Offset { get; init; }

    /// <summary>The number of bytes in the trailing part: greater than 0.</summary>
    public int Size { get; init; }
}
