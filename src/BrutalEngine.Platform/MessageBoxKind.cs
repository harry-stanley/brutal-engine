namespace BrutalEngine.Platform;

/// <summary>The kind of a message box, which selects the icon the OS shows with it.</summary>
/// <remarks>The values are fixed: new kinds are appended, never renumbered.</remarks>
public enum MessageBoxKind
{
    /// <summary>An informational message. The default.</summary>
    Information = 0,

    /// <summary>A warning.</summary>
    Warning = 1,

    /// <summary>An error, for example a crash report.</summary>
    Error = 2,
}
