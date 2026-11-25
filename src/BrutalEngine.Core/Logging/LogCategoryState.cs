namespace BrutalEngine.Core.Logging;

/// <summary>The shared state behind every <see cref="LogCategory"/> copy for one name on one logger.</summary>
/// <remarks>The level is read without locking on every log call, so it's volatile. Writes go through the logger.</remarks>
internal sealed class LogCategoryState(Logger owner, string name, LogLevel level)
{
    private volatile LogLevel _level = level;

    public Logger Owner { get; } = owner;

    public string Name { get; } = name;

    public LogLevel Level
    {
        get => _level;
        set => _level = value;
    }

    /// <summary>Whether <paramref name="level"/> passes this category's filter. <see cref="LogLevel.None"/> never does.</summary>
    public bool IsEnabled(LogLevel level) => level >= _level && level < LogLevel.None;
}
