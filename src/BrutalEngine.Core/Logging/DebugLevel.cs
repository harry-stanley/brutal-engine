namespace BrutalEngine.Core.Logging;

/// <summary>Marker for <see cref="LogLevel.Debug"/>, used as the type argument of the Debug message handler.</summary>
/// <remarks>Empty and stateless.</remarks>
public readonly struct DebugLevel : ILogLevel
{
    /// <inheritdoc />
    public static LogLevel Level => LogLevel.Debug;
}
