using System;
using Brutal.SdlApi;
using Brutal.VulkanApi;

namespace HelloWindow;

/// <summary>How a failed run is reported.</summary>
internal static class FailureReport
{
    /// <summary>Returns whether a failure needs its full exception text (type, stack trace and inner exceptions) in
    /// the log, rather than only its message.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns><see langword="false"/> for typed failures whose message names the failing call or requirement;
    /// <see langword="true"/> for anything unexpected, including <see cref="SdlCallbackException"/>: it wraps whatever
    /// an SDL callback threw (its inner exception), so its own message doesn't say what went wrong.</returns>
    public static bool NeedsStackTrace(Exception exception) =>
        exception is SdlCallbackException || exception is not (SampleFailureException or VkResultError or SdlException);
}
