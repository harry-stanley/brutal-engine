using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace BrutalEngine.Core;

/// <summary>
/// The owner thread of an object that may only be used on one thread, and the check that a call is on it.
/// </summary>
/// <remarks>
/// <para>
/// An owner captures its thread once, with <see cref="CurrentThread"/>, and starts each owner-thread member with
/// <see cref="ThrowIfNotCurrentThread"/>. The check runs in every build: it is a null check of the API name plus
/// one integer comparison against <see cref="Environment.CurrentManagedThreadId"/>, and allocates nothing when it
/// passes.
/// </para>
/// <para>
/// Managed thread ids are reused after a thread exits, so an affinity captured on a thread that has since ended can
/// match a later thread. Capture it on a thread that outlives the object it guards (an owner thread).
/// </para>
/// <para>
/// <c>default(ThreadAffinity)</c> captures no thread and is invalid: <see cref="IsCurrentThread"/> and
/// <see cref="ThrowIfNotCurrentThread"/> throw <see cref="InvalidOperationException"/> on it, and
/// <see cref="ThreadId"/> is <c>0</c>, which is never a managed thread id.
/// </para>
/// <para>This type is immutable and safe to read and check from any thread.</para>
/// </remarks>
public readonly struct ThreadAffinity
{
    // 0 marks default(ThreadAffinity): managed thread ids start at 1.
    private readonly int _threadId;

    private ThreadAffinity(int threadId)
    {
        _threadId = threadId;
    }

    /// <summary>The managed thread id of the owner thread, or <c>0</c> for <c>default(ThreadAffinity)</c>.</summary>
    /// <remarks>Never throws, including on <c>default(ThreadAffinity)</c>. Allocates nothing.</remarks>
    public int ThreadId => _threadId;

    /// <summary>Whether the calling thread is the owner thread.</summary>
    /// <exception cref="InvalidOperationException">This is <c>default(ThreadAffinity)</c>.</exception>
    /// <remarks>Allocates nothing, except the exception when it throws.</remarks>
    public bool IsCurrentThread
    {
        get
        {
            int threadId = _threadId;
            if (threadId == 0)
            {
                ThrowDefault();
            }

            return threadId == Environment.CurrentManagedThreadId;
        }
    }

    /// <summary>Captures the calling thread as the owner thread.</summary>
    /// <returns>An affinity whose owner thread is the calling thread.</returns>
    /// <remarks>Allocates nothing.</remarks>
    public static ThreadAffinity CurrentThread() => new(Environment.CurrentManagedThreadId);

    /// <summary>Throws unless the calling thread is the owner thread.</summary>
    /// <param name="apiName">
    /// The member being called, for example <c>Window.SetTitle</c>, named in the exception. Pass a constant or
    /// <c>nameof</c> expression, so the check allocates nothing.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="apiName"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">This is <c>default(ThreadAffinity)</c>.</exception>
    /// <exception cref="WrongThreadException">
    /// The calling thread is not the owner thread. <see cref="WrongThreadException.ApiName"/> is
    /// <paramref name="apiName"/>, <see cref="WrongThreadException.ExpectedThreadId"/> is <see cref="ThreadId"/> and
    /// <see cref="WrongThreadException.ActualThreadId"/> is the calling thread's managed thread id.
    /// </exception>
    /// <remarks>
    /// Allocates nothing when it passes. Only the throw path allocates (the exception and its message).
    /// </remarks>
    public void ThrowIfNotCurrentThread(string apiName)
    {
        ArgumentNullException.ThrowIfNull(apiName);

        // Managed thread ids are never 0, so this one comparison also sends default(ThreadAffinity) to the throw path.
        int currentThreadId = Environment.CurrentManagedThreadId;
        if (_threadId != currentThreadId)
        {
            ThrowNotCurrentThread(apiName, _threadId, currentThreadId);
        }
    }

    // The throw helpers stay out of line, so the passing check inlines to a comparison and allocates nothing.
    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNotCurrentThread(string apiName, int expectedThreadId, int actualThreadId)
    {
        if (expectedThreadId == 0)
        {
            ThrowDefault();
        }

        throw new WrongThreadException(apiName, expectedThreadId, actualThreadId);
    }

    [DoesNotReturn]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowDefault() =>
        throw new InvalidOperationException(
            "This ThreadAffinity is default and has no owner thread. Create it with ThreadAffinity.CurrentThread().");
}
