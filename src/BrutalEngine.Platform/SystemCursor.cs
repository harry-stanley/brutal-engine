using System.Diagnostics.CodeAnalysis;

namespace BrutalEngine.Platform;

/// <summary>A mouse cursor shape the OS provides.</summary>
/// <remarks>
/// The shapes, and their values, are the first twelve of SDL 3's <c>SDL_SystemCursor</c>, in its order; SDL's
/// single-edge resize cursors aren't included. How each shape looks is the OS's choice. The values are fixed: new
/// shapes are appended, never renumbered.
/// </remarks>
public enum SystemCursor
{
    /// <summary>The OS's default cursor, usually an arrow.</summary>
    Default = 0,

    /// <summary>A text-selection cursor, usually an I-beam.</summary>
    Text = 1,

    /// <summary>A busy cursor that blocks interaction, usually an hourglass or a spinner.</summary>
    Wait = 2,

    /// <summary>A crosshair.</summary>
    Crosshair = 3,

    /// <summary>A busy cursor that still allows interaction, usually an arrow with an hourglass or a spinner.</summary>
    Progress = 4,

    /// <summary>A two-headed arrow pointing north-west and south-east, for resizing from a corner.</summary>
    ResizeNwse = 5,

    /// <summary>A two-headed arrow pointing north-east and south-west, for resizing from a corner.</summary>
    ResizeNesw = 6,

    /// <summary>A two-headed arrow pointing east and west, for resizing horizontally.</summary>
    ResizeEw = 7,

    /// <summary>A two-headed arrow pointing north and south, for resizing vertically.</summary>
    ResizeNs = 8,

    /// <summary>A four-headed arrow, for moving something.</summary>
    Move = 9,

    /// <summary>A "not allowed" cursor, usually a slashed circle.</summary>
    NotAllowed = 10,

    /// <summary>A pointing hand, for links and buttons.</summary>
    [SuppressMessage(
        "Naming",
        "CA1720:Identifier contains type name",
        Justification = "The design's name for the shape, after SDL's pointer cursor: a pointing hand, not a pointer type.")]
    Pointer = 11,
}
