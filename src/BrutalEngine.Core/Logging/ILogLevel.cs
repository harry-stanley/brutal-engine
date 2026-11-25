namespace BrutalEngine.Core.Logging;

/// <summary>
/// Implemented by the empty marker structs (<see cref="TraceLevel"/> ... <see cref="CriticalLevel"/>, and
/// <see cref="DynamicLevel"/>) that give each logging method of <c>LogCategory</c> its own handler type,
/// so the level is a compile-time constant.
/// </summary>
/// <remarks>Not meant to be implemented outside the engine.</remarks>
public interface ILogLevel
{
    /// <summary>The level this marker stands for.</summary>
    public static abstract LogLevel Level { get; }
}
