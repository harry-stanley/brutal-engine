using System;
using System.Globalization;

namespace BrutalEngine.Core;

/// <summary>
/// Thrown when a member that may only run on its owner thread is called from another thread.
/// </summary>
/// <remarks>
/// <para>
/// Calling an owner-thread member from the wrong thread is a programming error. Engine code checks the thread on
/// every such call, in every build, and throws this exception through
/// <see cref="WrongThreadException(string, int, int)"/>, naming the member and both managed thread ids (as reported
/// by <see cref="Environment.CurrentManagedThreadId"/>).
/// </para>
/// <para>
/// The three standard constructors exist for the usual exception patterns. They leave <see cref="ApiName"/> empty
/// and both thread ids <c>0</c>, which is never a managed thread id.
/// </para>
/// <para>
/// An instance is not modified by the engine after it is constructed; its properties are safe to read from any
/// thread. Constructing one allocates (the exception and its message).
/// </para>
/// </remarks>
public sealed class WrongThreadException : InvalidOperationException
{
    /// <summary>Creates the exception for a call to <paramref name="apiName"/> from the wrong thread.</summary>
    /// <param name="apiName">The member that was called, for example <c>Window.SetTitle</c>. May be empty.</param>
    /// <param name="expectedThreadId">The managed thread id of the owner thread.</param>
    /// <param name="actualThreadId">The managed thread id of the calling thread.</param>
    /// <exception cref="ArgumentNullException"><paramref name="apiName"/> is <see langword="null"/>.</exception>
    /// <remarks>The message names <paramref name="apiName"/> and both thread ids. Allocates the message.</remarks>
    public WrongThreadException(string apiName, int expectedThreadId, int actualThreadId)
        : base(FormatMessage(apiName, expectedThreadId, actualThreadId))
    {
        ApiName = apiName;
        ExpectedThreadId = expectedThreadId;
        ActualThreadId = actualThreadId;
    }

    /// <summary>
    /// Creates the exception with a default message. <see cref="ApiName"/> is empty and both thread ids are
    /// <c>0</c>.
    /// </summary>
    public WrongThreadException()
        : base("The member was called from a thread other than its owner thread.")
    {
    }

    /// <summary>
    /// Creates the exception with a custom message. <see cref="ApiName"/> is empty and both thread ids are
    /// <c>0</c>.
    /// </summary>
    /// <param name="message">The message.</param>
    public WrongThreadException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates the exception with a custom message and inner exception. <see cref="ApiName"/> is empty and both
    /// thread ids are <c>0</c>.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public WrongThreadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>The member that was called from the wrong thread, or empty if none was supplied.</summary>
    public string ApiName { get; } = string.Empty;

    /// <summary>The managed thread id of the owner thread, or <c>0</c> if none was supplied.</summary>
    public int ExpectedThreadId { get; }

    /// <summary>The managed thread id of the calling thread, or <c>0</c> if none was supplied.</summary>
    public int ActualThreadId { get; }

    private static string FormatMessage(string apiName, int expectedThreadId, int actualThreadId)
    {
        ArgumentNullException.ThrowIfNull(apiName);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{apiName} was called on managed thread {actualThreadId}, but it may only be called on its owner thread, managed thread {expectedThreadId}.");
    }
}
