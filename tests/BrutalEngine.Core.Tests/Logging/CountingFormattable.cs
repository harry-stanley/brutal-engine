using System;

namespace BrutalEngine.Core.Tests.Logging;

/// <summary>Formats as <c>x</c> and counts how many times it was formatted.</summary>
internal sealed class CountingFormattable : IUtf8SpanFormattable
{
    public int FormatCount { get; private set; }

    public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        FormatCount++;
        bytesWritten = 0;
        if (utf8Destination.IsEmpty)
        {
            return false;
        }

        utf8Destination[0] = (byte)'x';
        bytesWritten = 1;
        return true;
    }
}
