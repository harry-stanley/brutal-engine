using System;

namespace BrutalEngine.Graphics;

/// <summary>
/// Per-frame ring memory that <see cref="CommandList.AllocateTransient"/> returned: the CPU writes <see cref="Data"/>, and
/// the frame's GPU work reads it through <see cref="Buffer"/> at <see cref="Offset"/>, without a copy
/// (docs/design/graphics.md section 6).
/// </summary>
/// <remarks>
/// <para>
/// Valid only while its frame is open. <see cref="GraphicsDevice.SubmitFrame"/> makes <see cref="Buffer"/> stale (any
/// later use throws <see cref="Core.StaleHandleException"/>) and ends the right to write <see cref="Data"/>: the GPU
/// reads the memory from then on, and the ring reuses it once the frame has completed, so a write after the submit is a
/// race the device can't detect. <c>default(TransientAllocation)</c> has the null handle, offset 0 and no data.
/// </para>
/// <para>
/// A view, not an owner: the device owns the memory and the alias handle, and never lets the handle be destroyed. The
/// handle is used on the device's owner thread only; the bytes may be written from any thread until the submit, for
/// example by job workers the owner thread waits for (docs/design/graphics.md section 14). No member allocates.
/// </para>
/// </remarks>
public readonly ref struct TransientAllocation
{
    internal TransientAllocation(BufferHandle buffer, long offset, Span<byte> data)
    {
        Buffer = buffer;
        Offset = offset;
        Data = data;
    }

    /// <summary>
    /// The buffer the memory lives in: a per-frame alias of the slot's upload ring, or of a dedicated block when the ring
    /// was full. Stale once the frame is submitted.
    /// </summary>
    /// <remarks>
    /// Every allocation of a frame from the same block shares this handle. It can be bound as any
    /// <see cref="TransientUsage"/> and copied from, never written by a copy or <see cref="GraphicsDevice.WriteBuffer"/>, and
    /// <see cref="GraphicsDevice.Destroy(BufferHandle)"/> refuses it.
    /// </remarks>
    public BufferHandle Buffer { get; }

    /// <summary>
    /// The byte offset of <see cref="Data"/> in <see cref="Buffer"/>: a multiple of 16, and of
    /// <see cref="GraphicsLimits.MinUniformBufferOffsetAlignment"/> for <see cref="TransientUsage.Uniform"/> and of
    /// <see cref="GraphicsLimits.MinStorageBufferOffsetAlignment"/> for <see cref="TransientUsage.Storage"/>.
    /// </summary>
    public long Offset { get; }

    /// <summary>
    /// The allocation's bytes, exactly as many as requested, in host-visible memory the GPU reads directly. Writable until
    /// the frame is submitted; the content starts undefined (whatever the ring last held).
    /// </summary>
    public Span<byte> Data { get; }
}
