namespace BrutalEngine.Graphics;

/// <summary>The form of an <see cref="AdapterSelection"/>: which argument it carries.</summary>
internal enum AdapterSelectionKind
{
    /// <summary>No override: the backend picks the best suitable adapter. The default value.</summary>
    Auto = 0,

    /// <summary>The adapter at an enumeration index.</summary>
    Index = 1,

    /// <summary>The adapters whose name contains a substring (ordinal, case-insensitive).</summary>
    Name = 2,

    /// <summary>The adapters of one <see cref="AdapterType"/>.</summary>
    Type = 3,
}
