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
/// <para>Thread-safe. No member allocates.</para>
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
