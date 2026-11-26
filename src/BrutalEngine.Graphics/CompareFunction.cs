namespace BrutalEngine.Graphics;

/// <summary>How a depth test or a depth-compare sampler compares a new value with the stored one.</summary>
/// <remarks>
/// Used by depth-compare samplers and by the depth state of pipelines (docs/design/graphics.md sections 5 and 7).
/// Backends map each value to their native one by name, never by casting.
/// </remarks>
public enum CompareFunction
{
    /// <summary>The comparison never passes. The default value.</summary>
    Never = 0,

    /// <summary>Passes when the new value is less than the stored one.</summary>
    Less = 1,

    /// <summary>Passes when the new value equals the stored one.</summary>
    Equal = 2,

    /// <summary>Passes when the new value is less than or equal to the stored one.</summary>
    LessOrEqual = 3,

    /// <summary>Passes when the new value is greater than the stored one.</summary>
    Greater = 4,

    /// <summary>Passes when the new value differs from the stored one.</summary>
    NotEqual = 5,

    /// <summary>Passes when the new value is greater than or equal to the stored one.</summary>
    GreaterOrEqual = 6,

    /// <summary>The comparison always passes.</summary>
    Always = 7,
}
