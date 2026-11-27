using System;
using Brutal.SdlApi;
using BrutalEngine.Core;

namespace BrutalEngine.Platform.Sdl3;

/// <summary>
/// The OS's system cursors for one platform (docs/design/platform.md section 1, "Input-related window state"): each
/// <see cref="SystemCursor"/> shape is created on first use, kept, and freed at <see cref="Dispose"/>.
/// </summary>
/// <remarks>
/// <para>
/// Not thread-safe: the owning platform calls it on its main thread, and disposes it before its video session, because
/// SDL frees its cursors itself at <c>SDL_Quit</c>. Setting a shape that already exists, and
/// <see cref="SetVisible"/>, allocate nothing on success; creating a shape and any failure (the message) allocate.
/// </para>
/// <para>
/// SDL's <c>dummy</c> and <c>offscreen</c> drivers create no cursors (observed: <c>SDL_CreateSystemCursor</c> fails with
/// "CreateSystemCursor is not currently supported"), so <see cref="Set"/> fails with <see cref="ErrorKind.Native"/>
/// there; <c>x11</c> and <c>wayland</c> create every shape.
/// </para>
/// </remarks>
internal sealed class SdlCursors : IDisposable
{
    // SystemCursor's values are SDL's first twelve, 0 to Pointer.
    private const int ShapeCount = (int)SystemCursor.Pointer + 1;

    private readonly INative _native;

    // The created cursors by shape; a null handle until created, and again after Dispose.
    private readonly SdlCursor[] _cursors = new SdlCursor[ShapeCount];
    private bool _disposed;

    /// <summary>
    /// The SDL cursor calls, with SDL's signatures (a null handle or a 0 status is failure), so tests can log them with
    /// a fake; <see cref="SdlCursors()"/> uses SDL's own.
    /// </summary>
    internal interface INative
    {
        /// <summary><c>SDL_CreateSystemCursor</c>: a new cursor, which the caller destroys.</summary>
        public SdlCursor CreateSystemCursor(SdlSystemCursor id);

        /// <summary><c>SDL_SetCursor</c>: makes a created cursor the active one.</summary>
        public byte SetCursor(SdlCursor cursor);

        /// <summary><c>SDL_DestroyCursor</c>: frees a created cursor.</summary>
        public void DestroyCursor(SdlCursor cursor);

        /// <summary><c>SDL_ShowCursor</c>.</summary>
        public byte ShowCursor();

        /// <summary><c>SDL_HideCursor</c>.</summary>
        public byte HideCursor();
    }

    /// <summary>Creates the cursor set over SDL's own cursor calls.</summary>
    public SdlCursors()
        : this(new SdlNative())
    {
    }

    /// <summary>Creates the cursor set over the given cursor calls: a test seam.</summary>
    /// <param name="native">The calls to make. Not owned.</param>
    /// <exception cref="ArgumentNullException"><paramref name="native"/> is <see langword="null"/>.</exception>
    internal SdlCursors(INative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    /// <summary>Maps a shape to SDL's system cursor id, which has the same value.</summary>
    /// <param name="cursor">The shape.</param>
    /// <returns>SDL's id for the shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cursor"/> isn't a <see cref="SystemCursor"/> value.</exception>
    internal static SdlSystemCursor ToSdl(SystemCursor cursor)
    {
        // Unsigned, so a negative value is out of range too.
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)cursor, (uint)ShapeCount, nameof(cursor));
        return (SdlSystemCursor)(uint)cursor;
    }

    /// <summary>Makes a shape the active cursor, creating it first if this set hasn't yet.</summary>
    /// <param name="cursor">The shape.</param>
    /// <returns>
    /// Success; or <see cref="ErrorKind.Native"/> with SDL's message if SDL can't create the shape
    /// (<c>SDL_CreateSystemCursor</c>) or refuses it (<c>SDL_SetCursor</c>).
    /// </returns>
    /// <remarks>
    /// A shape SDL created is kept until <see cref="Dispose"/>, even if setting it then fails. A shape SDL couldn't
    /// create isn't kept: the next call tries again.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="cursor"/> isn't a <see cref="SystemCursor"/> value.</exception>
    /// <exception cref="ObjectDisposedException">The set has been disposed.</exception>
    public Result Set(SystemCursor cursor)
    {
        SdlSystemCursor id = ToSdl(cursor);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ref SdlCursor slot = ref _cursors[(int)cursor];
        if (slot.IsNull())
        {
            SdlCursor created = _native.CreateSystemCursor(id);
            if (created.IsNull())
            {
                return SdlCheck.Error("SDL_CreateSystemCursor");
            }

            slot = created;
        }

        return SdlCheck.Check(_native.SetCursor(slot), "SDL_SetCursor");
    }

    /// <summary>Shows or hides the cursor, through <c>SDL_ShowCursor</c> or <c>SDL_HideCursor</c>.</summary>
    /// <param name="visible"><see langword="true"/> to show it.</param>
    /// <returns>Success, or <see cref="ErrorKind.Native"/> with SDL's message if SDL refused.</returns>
    /// <exception cref="ObjectDisposedException">The set has been disposed.</exception>
    public Result SetVisible(bool visible)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return visible
            ? SdlCheck.Check(_native.ShowCursor(), "SDL_ShowCursor")
            : SdlCheck.Check(_native.HideCursor(), "SDL_HideCursor");
    }

    /// <summary>Frees every cursor this set created, once; later calls do nothing.</summary>
    /// <remarks>
    /// SDL puts its default cursor back when the active one is freed. Every other member throws
    /// <see cref="ObjectDisposedException"/> afterwards.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (int i = 0; i < _cursors.Length; i++)
        {
            if (!_cursors[i].IsNull())
            {
                _native.DestroyCursor(_cursors[i]);
                _cursors[i] = default;
            }
        }
    }

    // SDL's own cursor calls.
    private sealed class SdlNative : INative
    {
        public SdlCursor CreateSystemCursor(SdlSystemCursor id) => Sdl.PInvoke.CreateSystemCursor(id);

        public byte SetCursor(SdlCursor cursor) => Sdl.PInvoke.SetCursor(cursor);

        public void DestroyCursor(SdlCursor cursor) => Sdl.PInvoke.DestroyCursor(cursor);

        public byte ShowCursor() => Sdl.PInvoke.ShowCursor();

        public byte HideCursor() => Sdl.PInvoke.HideCursor();
    }
}
