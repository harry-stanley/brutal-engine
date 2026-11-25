namespace BrutalEngine.Core.Logging;

/// <summary>
/// Marker for messages whose level is only known at run time (<c>LogCategory.Log(level, ...)</c>). The level is
/// passed to the handler's constructor instead.
/// </summary>
/// <remarks>Empty and stateless.</remarks>
public readonly struct DynamicLevel : ILogLevel
{
    /// <summary>
    /// <see cref="LogLevel.None"/>: a dynamic-level handler created without an explicit level is always disabled.
    /// </summary>
    public static LogLevel Level => LogLevel.None;
}
