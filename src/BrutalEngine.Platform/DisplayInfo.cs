using System.Globalization;

namespace BrutalEngine.Platform;

/// <summary>A display (monitor) connected when the platform last listed its displays.</summary>
/// <remarks>
/// <para>
/// The platform builds these from the backend's display queries, at creation and whenever a display event is pumped,
/// so a value is a snapshot: the display may have changed or been unplugged since.
/// </para>
/// <para>
/// Init-only properties rather than positional parameters, so properties can be added without breaking construction.
/// Equality compares every property; <see cref="Name"/> compares ordinally, and <see cref="ContentScale"/> compares as
/// <see cref="float.Equals(float)"/> does, which is exact because the values come from the backend unchanged.
/// Immutable and safe to share between threads. Creating one allocates; reading it doesn't. <c>ToString</c> allocates
/// and formats with the invariant culture.
/// </para>
/// </remarks>
public sealed record DisplayInfo
{
    /// <summary>The display's id, valid while it stays connected. Required.</summary>
    public required DisplayId Id { get; init; }

    /// <summary>The display's name as the OS reports it, often the monitor's model. Not necessarily unique. Required.</summary>
    public required string Name { get; init; }

    /// <summary>The display's area on the desktop, in window coordinates. Required.</summary>
    public required DisplayRect Bounds { get; init; }

    /// <summary>
    /// The part of <see cref="Bounds"/> that windows can use: without taskbars, docks and menu bars. Required.
    /// </summary>
    public required DisplayRect UsableBounds { get; init; }

    /// <summary>
    /// The display's content scale: how many pixels one content unit takes on it, for example 2.0 at 200 % on Windows.
    /// Required.
    /// </summary>
    /// <remarks>A new window's content size is converted with it (see <see cref="WindowOptions.ContentSize"/>).</remarks>
    public required float ContentScale { get; init; }

    /// <summary>The display's current mode: the desktop's resolution and refresh rate. Required.</summary>
    /// <remarks>
    /// The current mode may be missing from the display's list of fullscreen modes: SDL doesn't guarantee that its
    /// fullscreen list contains the desktop mode. So passing it to <see cref="IWindow.SetExclusiveFullscreen"/> can fail
    /// with <see cref="BrutalEngine.Core.ErrorKind.NotFound"/>; for exclusive fullscreen, pick a mode from the
    /// platform's fullscreen-mode list instead.
    /// </remarks>
    public required DisplayMode CurrentMode { get; init; }

    /// <summary>
    /// Whether this is the primary display, where a window opens when <see cref="WindowOptions.Display"/> is
    /// <see langword="null"/>. Default <see langword="false"/>.
    /// </summary>
    public bool IsPrimary { get; init; }

    /// <summary>
    /// Returns every property in the record's usual form, for example
    /// <c>DisplayInfo { Id = DisplayId { Value = 1 }, Name = DELL U2720Q, Bounds = DisplayRect { X = 0, Y = 0, Width = 3840, Height = 2160 }, UsableBounds = ..., ContentScale = 1.5, CurrentMode = DisplayMode { ... }, IsPrimary = True }</c>.
    /// </summary>
    /// <returns>A diagnostic string, formatted with the invariant culture whatever the current culture is. Allocates.</returns>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"DisplayInfo {{ Id = {Id}, Name = {Name}, Bounds = {Bounds}, UsableBounds = {UsableBounds}, ContentScale = {ContentScale}, CurrentMode = {CurrentMode}, IsPrimary = {IsPrimary} }}");
}
