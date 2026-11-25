using System;

namespace HelloWindow;

/// <summary>An expected failure whose message already says everything the user needs, for example a missing
/// validation layer, no suitable device, or <c>--headless</c> without <c>VK_EXT_headless_surface</c>.</summary>
/// <remarks>The sample prints only the message for these, and the full exception (with stack trace) for anything
/// unexpected.</remarks>
internal sealed class SampleFailureException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="SampleFailureException"/> class.</summary>
    public SampleFailureException()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SampleFailureException"/> class.</summary>
    /// <param name="message">What failed and, where it helps, what to do about it.</param>
    public SampleFailureException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="SampleFailureException"/> class.</summary>
    /// <param name="message">What failed.</param>
    /// <param name="innerException">The underlying failure.</param>
    public SampleFailureException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
