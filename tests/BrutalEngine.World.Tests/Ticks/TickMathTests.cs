using BrutalEngine.World.Ticks;
using Xunit;

namespace BrutalEngine.World.Tests.Ticks;

/// <summary>
/// The wrap-safe tick arithmetic (docs/design/world.md, section 5, "Wrap-safe comparison"). Expected values are worked
/// by hand from the note's rules: newer is a signed difference of 1 to 2³¹ - 1, the clamp period is 2³⁰, the stamp
/// floor is <c>tick - 2³⁰ + 1</c> and the consumer floor one less.
/// </summary>
public sealed class TickMathTests
{
    [Theory]
    [InlineData(2u, 1u, true)]
    [InlineData(1u, 1u, false)]
    [InlineData(1u, 2u, false)]
    [InlineData(0u, uint.MaxValue, true)] // 0 is one tick after uint.MaxValue: the wrap row.
    [InlineData(5u, 0xFFFF_FFF0u, true)] // 21 ticks ahead, across the wrap.
    [InlineData(uint.MaxValue, 0u, false)] // one tick behind, across the wrap.
    [InlineData(0x7FFF_FFFFu, 0u, true)] // 2³¹ - 1 ahead: the largest valid age.
    [InlineData(0x8000_0005u, 6u, true)] // 2³¹ - 1 ahead from a non-zero base.
    [InlineData(0x8000_0000u, 0u, false)] // 2³¹ ahead: outside the valid range, so it reads as behind.
    [InlineData(0x8000_0001u, 0u, false)]
    public void IsNewer_WrapCases(uint tick, uint since, bool expected) =>
        Assert.Equal(expected, TickMath.IsNewer(tick, since));

    [Theory]
    [InlineData(1u, 2u, true)]
    [InlineData(2u, 2u, false)] // the floor itself isn't older
    [InlineData(3u, 2u, false)]
    [InlineData(uint.MaxValue, 1u, true)] // two ticks before the floor, across the wrap
    [InlineData(1u, uint.MaxValue, false)] // newer than the floor, across the wrap
    [InlineData(0x4000_0000u, 0x8000_0001u, true)]
    public void IsOlderThanFloor_WrapCases(uint tick, uint floor, bool expected) =>
        Assert.Equal(expected, TickMath.IsOlderThanFloor(tick, floor));

    [Theory]
    [InlineData(0u, true)]
    [InlineData(0x4000_0000u, true)]
    [InlineData(0x8000_0000u, true)]
    [InlineData(0xC000_0000u, true)]
    [InlineData(1u, false)]
    [InlineData(0x2000_0000u, false)] // 2²⁹: a power of two, but not a multiple of 2³⁰
    [InlineData(0x3FFF_FFFFu, false)]
    [InlineData(0x4000_0001u, false)]
    [InlineData(0xBFFF_FFFFu, false)]
    [InlineData(uint.MaxValue, false)]
    public void IsClampTick_MultiplesOfInterval(uint tick, bool expected) =>
        Assert.Equal(expected, TickMath.IsClampTick(tick));

    [Theory]
    [InlineData(0x4000_0000u, 1u, 0u)]
    [InlineData(0x8000_0000u, 0x4000_0001u, 0x4000_0000u)]
    [InlineData(0xC000_0000u, 0x8000_0001u, 0x8000_0000u)]
    [InlineData(0u, 0xC000_0001u, 0xC000_0000u)] // the boundary at the wrap: floors below zero
    public void Floors_AtEachBoundary(uint tick, uint stampFloor, uint consumerFloor)
    {
        Assert.Equal(stampFloor, TickMath.StampFloor(tick));
        Assert.Equal(consumerFloor, TickMath.ConsumerFloor(tick));
    }

    [Fact]
    public void ClampInterval_Is2Pow30() => Assert.Equal(1_073_741_824u, TickMath.ClampInterval);
}
