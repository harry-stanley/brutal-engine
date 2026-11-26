namespace BrutalEngine.Graphics.Backend;

/// <summary>
/// Where a backend places a buffer's memory, chosen by the front end from the buffer's <see cref="MemoryIntent"/> or for
/// its own upload and readback buffers (docs/design/graphics.md, "The backend interface").
/// </summary>
/// <remarks>
/// <para>
/// <see cref="HostUpload"/> and <see cref="HostReadback"/> buffers are mapped persistently: the backend returns the
/// pointer in <see cref="BackendBuffer.MappedPointer"/>, valid until the buffer is destroyed. The other two kinds are never
/// mapped. For host memory the backend makes CPU writes visible to the GPU work of the next <c>Execute</c>, and GPU
/// writes visible to the CPU once the frame that made them is reported complete (coherent memory or a flush and
/// invalidate, and for GPU writes a host-read memory dependency before the frame's completion signal).
/// </para>
/// <para>
/// The numeric values are fixed. They are no native API's values, so backends map them by name, never by casting.
/// </para>
/// </remarks>
public enum BackendMemory
{
    /// <summary>Device-local memory the CPU never maps: <see cref="MemoryIntent.GpuOnly"/> buffers. The default value.</summary>
    DeviceLocal = 0,

    /// <summary>
    /// Device-local memory, host-visible when the adapter has such memory: <see cref="MemoryIntent.Upload"/> buffers. A
    /// placement hint only: the buffer is never mapped, and is written through the upload ring like
    /// <see cref="DeviceLocal"/>.
    /// </summary>
    DeviceLocalHostVisiblePreferred = 1,

    /// <summary>
    /// Host-visible, coherent memory the CPU writes sequentially and the GPU reads: the front end's upload ring and
    /// dedicated upload blocks. Mapped persistently.
    /// </summary>
    HostUpload = 2,

    /// <summary>
    /// Host-visible memory, cached when the adapter has it, that the GPU writes and the CPU reads:
    /// <see cref="MemoryIntent.Readback"/> buffers. Mapped persistently.
    /// </summary>
    HostReadback = 3,
}
