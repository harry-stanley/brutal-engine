using System;

namespace BrutalEngine.Platform;

/// <summary>
/// The events of one pump, in the order the OS delivered them.
/// </summary>
/// <remarks>
/// <para>
/// A view of a <see cref="PlatformEventBuffer"/>, taken with <see cref="PlatformEventBuffer.AsBatch"/>. The batch
/// and its <see cref="Events"/> are valid until the buffer's next
/// <see cref="PlatformEventBuffer.Clear"/> (the next pump); reading them after that is a bug. Being a
/// <see langword="ref"/> struct keeps a batch out of fields. <c>default(PlatformEventBatch)</c> is an empty batch.
/// </para>
/// <para>
/// No member allocates, except the ones that throw. Use it on the thread that pumps, like its buffer.
/// </para>
/// </remarks>
public readonly ref struct PlatformEventBatch
{
    private readonly ReadOnlySpan<PlatformEvent> _events;

    internal PlatformEventBatch(ReadOnlySpan<PlatformEvent> events) => _events = events;

    /// <summary>Gets the pump's events, in the order the OS delivered them.</summary>
    /// <value>The events; empty when the pump found none.</value>
    public ReadOnlySpan<PlatformEvent> Events => _events;
}
