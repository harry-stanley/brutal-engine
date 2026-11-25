using System;
using System.Collections.Generic;
using System.Threading;

namespace BrutalEngine.Core.Logging;

/// <summary>
/// The engine logger: owns named categories and their levels, and passes every enabled entry to its sinks.
/// </summary>
/// <remarks>
/// <para>
/// One logger per host, created and disposed by it. There is no global instance (docs/03-architecture.md, 3.21).
/// Code logs through the <see cref="LogCategory"/> values returned by <see cref="CreateCategory"/>.
/// </para>
/// <para>
/// Thread-safe: every member may be called from any thread. Category changes, flushing and dispatch to the
/// sinks are serialised by a lock, so each sink sees entries one at a time and in the same order.
/// </para>
/// <para>
/// The logger doesn't own its sinks. <see cref="Dispose"/> flushes them; whoever created them disposes them
/// afterwards. A sink that throws doesn't affect the other sinks or the caller (see <see cref="SinkFailureCount"/>).
/// </para>
/// <para>
/// After <see cref="Dispose"/>, every member throws <see cref="ObjectDisposedException"/>, except
/// <see cref="Dispose"/> itself and logging through a category, which silently does nothing. That way, shutdown
/// order can't crash code that logs late.
/// </para>
/// </remarks>
public sealed class Logger : IDisposable
{
    /// <summary>Maximum number of categories one logger holds.</summary>
    internal const int MaxCategories = 1024;

    private const int MaxCategoryNameLength = 64;

    private readonly Lock _lock = new();
    private readonly ILogSink[] _sinks;
    private readonly Dictionary<string, LogCategoryState> _categories = new(StringComparer.Ordinal);
    private readonly LogLevel _minimumLevel;
    private long _sinkFailureCount;
    private volatile bool _disposed;
    private bool _dispatching;

    /// <summary>Creates a logger.</summary>
    /// <param name="options">Settings. The sink list is copied, so later changes to it have no effect.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <see cref="LoggerOptions.Sinks"/> is <see langword="null"/> or contains <see langword="null"/>, or
    /// <see cref="LoggerOptions.TimeProvider"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="LoggerOptions.MinimumLevel"/> isn't a defined <see cref="LogLevel"/>, or
    /// <see cref="LoggerOptions.MaxMessageBytes"/> is outside 64 to 65536.
    /// </exception>
    public Logger(LoggerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Sinks is null)
        {
            throw new ArgumentException("LoggerOptions.Sinks must not be null.", nameof(options));
        }

        if (options.TimeProvider is null)
        {
            throw new ArgumentException("LoggerOptions.TimeProvider must not be null.", nameof(options));
        }

        if (!Enum.IsDefined(options.MinimumLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MinimumLevel, "LoggerOptions.MinimumLevel is not a defined LogLevel.");
        }

        if (options.MaxMessageBytes is < LoggerOptions.SmallestMaxMessageBytes or > LoggerOptions.LargestMaxMessageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxMessageBytes,
                $"LoggerOptions.MaxMessageBytes must be from {LoggerOptions.SmallestMaxMessageBytes} to {LoggerOptions.LargestMaxMessageBytes}.");
        }

        var sinks = new ILogSink[options.Sinks.Count];
        for (int i = 0; i < sinks.Length; i++)
        {
            sinks[i] = options.Sinks[i] ?? throw new ArgumentException($"LoggerOptions.Sinks[{i}] is null.", nameof(options));
        }

        _sinks = sinks;
        _minimumLevel = options.MinimumLevel;
        TimeProvider = options.TimeProvider;
        MaxMessageBytes = options.MaxMessageBytes;
    }

    /// <summary>
    /// Number of times a sink threw from <see cref="ILogSink.Write"/> or <see cref="ILogSink.Flush"/>. Those
    /// exceptions are caught so they can't stop other sinks or the code that logged.
    /// </summary>
    /// <remarks>
    /// Thread-safe; doesn't allocate. Like every member except <see cref="Dispose"/> and logging, it throws once the
    /// logger is disposed: read it before disposing, e.g. for a shutdown report.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The logger is disposed.</exception>
    public long SinkFailureCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Interlocked.Read(ref _sinkFailureCount);
        }
    }

    internal TimeProvider TimeProvider { get; }

    internal int MaxMessageBytes { get; }

    /// <summary>Returns the category with this name, creating it at the minimum level the first time.</summary>
    /// <param name="name">1 to 64 characters from <c>A-Z</c>, <c>a-z</c>, <c>0-9</c>, <c>.</c>, <c>_</c> and <c>-</c>. Case-sensitive.</param>
    /// <returns>The category. Equal to every other category this logger returned for the same name.</returns>
    /// <remarks>Allocates when the category is new.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty, too long, or has a character outside the set.</exception>
    /// <exception cref="InvalidOperationException">The name is new and the logger already holds 1024 categories.</exception>
    /// <exception cref="ObjectDisposedException">The logger is disposed.</exception>
    public LogCategory CreateCategory(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateCategoryName(name);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_categories.TryGetValue(name, out LogCategoryState? state))
            {
                if (_categories.Count >= MaxCategories)
                {
                    throw new InvalidOperationException(
                        $"The logger already has {MaxCategories} categories, its maximum, so '{name}' was not created. Reuse categories instead of creating them per object or per message.");
                }

                state = new LogCategoryState(this, name, _minimumLevel);
                _categories.Add(name, state);
            }

            return new LogCategory(state);
        }
    }

    /// <summary>Sets the level of one category, overriding <see cref="LoggerOptions.MinimumLevel"/> for it.</summary>
    /// <param name="category">A category created by this logger.</param>
    /// <param name="level">The new level. <see cref="LogLevel.None"/> turns the category off.</param>
    /// <remarks>Takes effect for log calls that start after it returns. Doesn't allocate.</remarks>
    /// <exception cref="ArgumentException"><paramref name="category"/> is <c>default</c> or from another logger.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> isn't a defined <see cref="LogLevel"/>.</exception>
    /// <exception cref="ObjectDisposedException">The logger is disposed.</exception>
    public void SetLevel(LogCategory category, LogLevel level)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        LogCategoryState state = OwnState(category, nameof(category));

        // A range check, not Enum.IsDefined: IsDefined reads a cache that the runtime holds only weakly, so after
        // any garbage collection its next call rebuilds the cache and allocates. LogLevel is a byte enum with
        // contiguous members from 0 to None (LogLevelValidationTests guards that).
        if (level > LogLevel.None)
        {
            throw new ArgumentOutOfRangeException(nameof(level), level, "Not a defined LogLevel.");
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            state.Level = level;
        }
    }

    /// <summary>Whether an entry at <paramref name="level"/> would be logged through <paramref name="category"/>.</summary>
    /// <param name="category">A category created by this logger.</param>
    /// <param name="level">The level to check.</param>
    /// <returns>The same as <see cref="LogCategory.IsEnabled"/>. Doesn't allocate.</returns>
    /// <exception cref="ArgumentException"><paramref name="category"/> is <c>default</c> or from another logger.</exception>
    /// <exception cref="ObjectDisposedException">The logger is disposed.</exception>
    public bool IsEnabled(LogCategory category, LogLevel level)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return OwnState(category, nameof(category)).IsEnabled(level);
    }

    /// <summary>Flushes every sink, in order. A sink that throws is counted in <see cref="SinkFailureCount"/>.</summary>
    /// <exception cref="ObjectDisposedException">The logger is disposed.</exception>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            FlushSinks();
        }
    }

    /// <summary>
    /// Flushes the sinks (without disposing them), turns every category off, and makes later log calls no-ops.
    /// Calling it again does nothing.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (LogCategoryState state in _categories.Values)
            {
                state.Level = LogLevel.None;
            }

            FlushSinks();
        }
    }

    /// <summary>Passes one formatted entry to every sink. Called by <see cref="LogMessageHandler{TLevel}"/>.</summary>
    /// <remarks>
    /// The timestamp is taken inside the lock, so timestamps never go backwards in delivery order. An entry
    /// logged by a sink while it is being called (on the same thread, so the lock is already held) is dropped
    /// instead of recursing.
    /// </remarks>
    internal void Dispatch(LogCategoryState category, LogLevel level, ReadOnlySpan<byte> message, string callerFile, int callerLine)
    {
        lock (_lock)
        {
            if (_disposed || _dispatching)
            {
                return;
            }

            _dispatching = true;
            try
            {
                LogEntry entry = new()
                {
                    Level = level,
                    Category = category.Name,
                    Timestamp = TimeProvider.GetUtcNow(),
                    ThreadId = Environment.CurrentManagedThreadId,
                    Message = message,
                    CallerFilePath = callerFile,
                    CallerLineNumber = callerLine,
                };

                foreach (ILogSink sink in _sinks)
                {
                    try
                    {
                        sink.Write(in entry);
                    }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    {
                        _ = Interlocked.Increment(ref _sinkFailureCount);
                    }
                }
            }
            finally
            {
                _dispatching = false;
            }
        }
    }

    private static void ValidateCategoryName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length is 0 or > MaxCategoryNameLength)
        {
            throw new ArgumentException($"A category name must be 1 to {MaxCategoryNameLength} characters long.", nameof(name));
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                throw new ArgumentException(
                    $"Category name '{name}' has a character outside A-Z, a-z, 0-9, '.', '_' and '-'.", nameof(name));
            }
        }
    }

    private LogCategoryState OwnState(LogCategory category, string paramName)
    {
        LogCategoryState? state = category.State;
        if (state is null || !ReferenceEquals(state.Owner, this))
        {
            throw new ArgumentException("The category was not created by this logger.", paramName);
        }

        return state;
    }

    // Called with _lock held.
    private void FlushSinks()
    {
        foreach (ILogSink sink in _sinks)
        {
            try
            {
                sink.Flush();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _ = Interlocked.Increment(ref _sinkFailureCount);
            }
        }
    }
}
