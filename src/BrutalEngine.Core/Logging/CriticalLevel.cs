namespace BrutalEngine.Core.Logging;

/// <summary>Marker for <see cref="LogLevel.Critical"/>, used as the type argument of the Critical message handler.</summary>
/// <remarks>Empty and stateless.</remarks>
public readonly struct CriticalLevel : ILogLevel
{
    /// <inheritdoc />
    public static LogLevel Level => LogLevel.Critical;
}
