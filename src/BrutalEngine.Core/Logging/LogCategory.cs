using System;
using System.Runtime.CompilerServices;

namespace BrutalEngine.Core.Logging;

/// <summary>A named category of one <see cref="Logger"/>: the unit that log calls go through and levels filter by.</summary>
/// <remarks>
/// <para>
/// Create categories with <see cref="Logger.CreateCategory"/>, usually once per subsystem, and keep them in a
/// field. A category is a small copyable handle: copies, and categories created again with the same name,
/// are equal and share the same level.
/// </para>
/// <para>
/// <c>default(LogCategory)</c> is a valid no-op category: it logs nothing, <see cref="IsEnabled"/> is always
/// <see langword="false"/>, and it never throws.
/// </para>
/// <para>
/// Log with the level methods, e.g. <c>category.Information($"Loaded {count} assets in {ms:F1} ms")</c>. The
/// compiler builds a <see cref="LogMessageHandler{TLevel}"/> that formats nothing unless the level is enabled.
/// Logging after the logger is disposed does nothing.
/// </para>
/// <para>Thread-safe. No member allocates, except as described on <see cref="LogMessageHandler{TLevel}"/>.</para>
/// </remarks>
public readonly struct LogCategory : IEquatable<LogCategory>
{
    private readonly LogCategoryState? _state;

    internal LogCategory(LogCategoryState state) => _state = state;

    /// <summary>The category name, or an empty string for <c>default(LogCategory)</c>.</summary>
    public string Name => _state?.Name ?? string.Empty;

    internal LogCategoryState? State => _state;

    /// <summary>Whether an entry at <paramref name="level"/> would be logged through this category.</summary>
    /// <param name="level">The level to check.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="level"/> is at or above the category's level and isn't
    /// <see cref="LogLevel.None"/>. Always <see langword="false"/> for <c>default(LogCategory)</c> and after the
    /// logger is disposed. Never throws.
    /// </returns>
    public bool IsEnabled(LogLevel level) => _state is not null && _state.IsEnabled(level);

    /// <summary>Logs a <see cref="LogLevel.Trace"/> message: very detailed tracing.</summary>
    /// <param name="message">
    /// The message, as an interpolated string. Its parts are formatted only if the level is enabled.
    /// </param>
    /// <param name="callerFile">Filled in by the compiler.</param>
    /// <param name="callerLine">Filled in by the compiler.</param>
    /// <remarks>
    /// No-op when disabled. Never throws, except from code the caller supplied (a value's own formatting code,
    /// or the logger's <see cref="System.TimeProvider"/>), or with a handler built by hand for another category or
    /// level (<see cref="ArgumentException"/>).
    /// </remarks>
    public void Trace(
        [InterpolatedStringHandlerArgument("")] ref LogMessageHandler<TraceLevel> message,
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => message.Complete(_state, LogLevel.Trace, callerFile, callerLine);

    /// <summary>Logs a <see cref="LogLevel.Debug"/> message: diagnostic detail.</summary>
    /// <param name="message">
    /// The message, as an interpolated string. Its parts are formatted only if the level is enabled.
    /// </param>
    /// <param name="callerFile">Filled in by the compiler.</param>
    /// <param name="callerLine">Filled in by the compiler.</param>
    /// <remarks>
    /// No-op when disabled. Never throws, except from code the caller supplied (a value's own formatting code,
    /// or the logger's <see cref="System.TimeProvider"/>), or with a handler built by hand for another category or
    /// level (<see cref="ArgumentException"/>).
    /// </remarks>
    public void Debug(
        [InterpolatedStringHandlerArgument("")] ref LogMessageHandler<DebugLevel> message,
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => message.Complete(_state, LogLevel.Debug, callerFile, callerLine);

    /// <summary>Logs a <see cref="LogLevel.Information"/> message: normal operation.</summary>
    /// <param name="message">
    /// The message, as an interpolated string. Its parts are formatted only if the level is enabled.
    /// </param>
    /// <param name="callerFile">Filled in by the compiler.</param>
    /// <param name="callerLine">Filled in by the compiler.</param>
    /// <remarks>
    /// No-op when disabled. Never throws, except from code the caller supplied (a value's own formatting code,
    /// or the logger's <see cref="System.TimeProvider"/>), or with a handler built by hand for another category or
    /// level (<see cref="ArgumentException"/>).
    /// </remarks>
    public void Information(
        [InterpolatedStringHandlerArgument("")] ref LogMessageHandler<InformationLevel> message,
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => message.Complete(_state, LogLevel.Information, callerFile, callerLine);

    /// <summary>Logs a <see cref="LogLevel.Warning"/> message: something unexpected that was recovered from.</summary>
    /// <param name="message">
    /// The message, as an interpolated string. Its parts are formatted only if the level is enabled.
    /// </param>
    /// <param name="callerFile">Filled in by the compiler.</param>
    /// <param name="callerLine">Filled in by the compiler.</param>
    /// <remarks>
    /// No-op when disabled. Never throws, except from code the caller supplied (a value's own formatting code,
    /// or the logger's <see cref="System.TimeProvider"/>), or with a handler built by hand for another category or
    /// level (<see cref="ArgumentException"/>).
    /// </remarks>
    public void Warning(
        [InterpolatedStringHandlerArgument("")] ref LogMessageHandler<WarningLevel> message,
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => message.Complete(_state, LogLevel.Warning, callerFile, callerLine);

    /// <summary>Logs a <see cref="LogLevel.Error"/> message: a failed operation.</summary>
    /// <param name="message">
    /// The message, as an interpolated string. Its parts are formatted only if the level is enabled.
    /// </param>
    /// <param name="callerFile">Filled in by the compiler.</param>
    /// <param name="callerLine">Filled in by the compiler.</param>
    /// <remarks>
    /// No-op when disabled. Never throws, except from code the caller supplied (a value's own formatting code,
    /// or the logger's <see cref="System.TimeProvider"/>), or with a handler built by hand for another category or
    /// level (<see cref="ArgumentException"/>).
    /// </remarks>
    public void Error(
        [InterpolatedStringHandlerArgument("")] ref LogMessageHandler<ErrorLevel> message,
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => message.Complete(_state, LogLevel.Error, callerFile, callerLine);

    /// <summary>Logs a <see cref="LogLevel.Critical"/> message: a failure that likely ends the process or session.</summary>
    /// <param name="message">
    /// The message, as an interpolated string. Its parts are formatted only if the level is enabled.
    /// </param>
    /// <param name="callerFile">Filled in by the compiler.</param>
    /// <param name="callerLine">Filled in by the compiler.</param>
    /// <remarks>
    /// No-op when disabled. Never throws, except from code the caller supplied (a value's own formatting code,
    /// or the logger's <see cref="System.TimeProvider"/>), or with a handler built by hand for another category or
    /// level (<see cref="ArgumentException"/>).
    /// </remarks>
    public void Critical(
        [InterpolatedStringHandlerArgument("")] ref LogMessageHandler<CriticalLevel> message,
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => message.Complete(_state, LogLevel.Critical, callerFile, callerLine);

    /// <summary>Logs a message at a level chosen at run time.</summary>
    /// <param name="level">
    /// The level. The compiler passes it to the message handler, which checks it. <see cref="LogLevel.None"/>
    /// logs nothing.
    /// </param>
    /// <param name="message">
    /// The message, as an interpolated string. Its parts are formatted only if the level is enabled.
    /// </param>
    /// <param name="callerFile">Filled in by the compiler.</param>
    /// <param name="callerLine">Filled in by the compiler.</param>
    /// <remarks>
    /// No-op when disabled. Never throws, except from code the caller supplied (a value's own formatting code,
    /// or the logger's <see cref="System.TimeProvider"/>), or with a handler built by hand for another category or
    /// level (<see cref="ArgumentException"/>).
    /// </remarks>
    public void Log(
        LogLevel level,
        [InterpolatedStringHandlerArgument("", nameof(level))] ref LogMessageHandler<DynamicLevel> message,
        [CallerFilePath] string callerFile = "",
        [CallerLineNumber] int callerLine = 0) => message.Complete(_state, level, callerFile, callerLine);

    /// <inheritdoc />
    public bool Equals(LogCategory other) => ReferenceEquals(_state, other._state);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LogCategory other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _state is null ? 0 : RuntimeHelpers.GetHashCode(_state);

    /// <summary>Compares two categories for equality (same logger and name).</summary>
    /// <param name="left">First category.</param>
    /// <param name="right">Second category.</param>
    /// <returns><see langword="true"/> if both are equal.</returns>
    public static bool operator ==(LogCategory left, LogCategory right) => left.Equals(right);

    /// <summary>Compares two categories for inequality.</summary>
    /// <param name="left">First category.</param>
    /// <param name="right">Second category.</param>
    /// <returns><see langword="true"/> if they differ.</returns>
    public static bool operator !=(LogCategory left, LogCategory right) => !left.Equals(right);
}
