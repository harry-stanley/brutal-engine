using System;

namespace BrutalEngine.Platform;

/// <summary>A modal message box with an OK button, for the platform's <c>ShowMessageBox</c>.</summary>
/// <param name="Kind">The kind of message, which selects the icon.</param>
/// <param name="Title">The box's title. Must not be <see langword="null"/>.</param>
/// <param name="Message">The message to show. Must not be <see langword="null"/>.</param>
/// <remarks>
/// <para>
/// The record doesn't check its values. The platform validates them when it shows the box: a <see langword="null"/>
/// request throws <see cref="ArgumentNullException"/>, a <see langword="null"/> <see cref="Title"/> or
/// <see cref="Message"/> throws <see cref="ArgumentException"/>, and an undefined <see cref="Kind"/> throws
/// <see cref="ArgumentOutOfRangeException"/>. A headless platform shows no box and fails with
/// <see cref="BrutalEngine.Core.ErrorKind.Unavailable"/>.
/// </para>
/// <para>
/// Equality compares every property; <see cref="Parent"/> compares by reference. Immutable; the platform reads it only
/// on the main thread, where <see cref="Parent"/> is usable. Creating one allocates; reading it doesn't.
/// </para>
/// </remarks>
public sealed record MessageBoxRequest(MessageBoxKind Kind, string Title, string Message)
{
    /// <summary>
    /// The window the box belongs to and is shown over, or <see langword="null"/> (the default) for a box of its own.
    /// </summary>
    /// <remarks>Not owned: showing the box doesn't dispose the window.</remarks>
    public IWindow? Parent { get; init; }
}
