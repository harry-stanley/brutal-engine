using System;
using System.Runtime.CompilerServices;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class PayloadLayoutTests
{
    // The 24-byte payload starts at offset 16 of the 40-byte event; first and last are the inclusive byte offsets of
    // each view in docs/design/platform.md section 2 ("Payloads, by kind").
    private const int PayloadStart = 16;
    private const int PayloadEnd = 39;

    [Theory]
    [InlineData(nameof(KeyEventData), 16, 27)]
    [InlineData(nameof(TextEventData), 16, 23)]
    [InlineData(nameof(MouseMotionData), 16, 35)]
    [InlineData(nameof(MouseButtonData), 16, 27)]
    [InlineData(nameof(MouseWheelData), 16, 31)]
    public void Sizes_FitTheirOffsets(string payload, int first, int last)
    {
        Assert.InRange(first, PayloadStart, PayloadEnd);
        Assert.InRange(last, first, PayloadEnd);
        Assert.Equal(last - first + 1, SizeOf(payload));
    }

    [Theory]
    [InlineData(nameof(KeyEventData))]
    [InlineData(nameof(TextEventData))]
    [InlineData(nameof(MouseMotionData))]
    [InlineData(nameof(MouseButtonData))]
    [InlineData(nameof(MouseWheelData))]
    public void Payloads_HoldNoReferences(string payload)
    {
        Assert.False(HoldsReferences(payload));
    }

    private static int SizeOf(string payload) => payload switch
    {
        nameof(KeyEventData) => Unsafe.SizeOf<KeyEventData>(),
        nameof(TextEventData) => Unsafe.SizeOf<TextEventData>(),
        nameof(MouseMotionData) => Unsafe.SizeOf<MouseMotionData>(),
        nameof(MouseButtonData) => Unsafe.SizeOf<MouseButtonData>(),
        nameof(MouseWheelData) => Unsafe.SizeOf<MouseWheelData>(),
        _ => throw new ArgumentOutOfRangeException(nameof(payload), payload, "Not a payload record."),
    };

    private static bool HoldsReferences(string payload) => payload switch
    {
        nameof(KeyEventData) => RuntimeHelpers.IsReferenceOrContainsReferences<KeyEventData>(),
        nameof(TextEventData) => RuntimeHelpers.IsReferenceOrContainsReferences<TextEventData>(),
        nameof(MouseMotionData) => RuntimeHelpers.IsReferenceOrContainsReferences<MouseMotionData>(),
        nameof(MouseButtonData) => RuntimeHelpers.IsReferenceOrContainsReferences<MouseButtonData>(),
        nameof(MouseWheelData) => RuntimeHelpers.IsReferenceOrContainsReferences<MouseWheelData>(),
        _ => throw new ArgumentOutOfRangeException(nameof(payload), payload, "Not a payload record."),
    };
}
