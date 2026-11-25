using System;

namespace BrutalEngine.Core.Tests.Logging;

/// <summary>A clock that always returns the same time.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
