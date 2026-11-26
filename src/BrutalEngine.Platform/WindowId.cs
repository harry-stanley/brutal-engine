namespace BrutalEngine.Platform;

/// <summary>Identifies a window: the value events carry to say which window they belong to.</summary>
/// <param name="Value">The backend's id for the window. <c>0</c> means no window.</param>
/// <remarks>
/// <para>
/// <c>default(WindowId)</c> (<see cref="Value"/> <c>0</c>) means no window. Platform-wide events carry it, and so do
/// key, text and mouse events that the OS reports without a window. Every live window has a non-zero id.
/// </para>
/// <para>
/// A plain value, so events that carry it stay unmanaged. Equality compares <see cref="Value"/>. Immutable and
/// safe to use from any thread. No member allocates, except <c>ToString</c>.
/// </para>
/// </remarks>
public readonly record struct WindowId(uint Value);
