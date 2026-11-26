namespace BrutalEngine.Graphics;

/// <summary>What the CPU does with a buffer's memory, which decides where the backend places it.</summary>
/// <remarks>
/// A hint about access, not a memory type: the backend chooses the memory (docs/design/graphics.md sections 5 and 6).
/// Writes to <see cref="GpuOnly"/> and <see cref="Upload"/> buffers have the same semantics.
/// </remarks>
public enum MemoryIntent
{
    /// <summary>Device-local memory, written through the upload ring. The default value.</summary>
    GpuOnly = 0,

    /// <summary>
    /// Rewritten often from the CPU. A placement hint: host-visible device memory when the adapter has it. Writes behave
    /// exactly as for <see cref="GpuOnly"/>.
    /// </summary>
    Upload = 1,

    /// <summary>
    /// Host-visible, cached memory: the destination of GPU-to-CPU copies, read back on the CPU. The buffer's only usage
    /// must be <see cref="BufferUsage.CopyDestination"/>.
    /// </summary>
    Readback = 2,
}
