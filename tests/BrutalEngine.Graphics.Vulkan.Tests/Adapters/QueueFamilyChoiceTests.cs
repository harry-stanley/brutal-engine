using System.Collections.Generic;
using BrutalEngine.Graphics.Vulkan.Adapters;
using Xunit;
using static BrutalEngine.Graphics.Vulkan.Tests.Adapters.Candidates;

namespace BrutalEngine.Graphics.Vulkan.Tests.Adapters;

public sealed class QueueFamilyChoiceTests
{
    // Each family's index is 10 + its position, so a choice that returned the position instead of the index fails.
    private static readonly Dictionary<string, (AdapterCandidate.QueueFamily[] Families, bool NeedsPresent, int Expected)> _cases = new()
    {
        ["no families"] = ([], false, -1),
        ["no families, present needed"] = ([], true, -1),
        ["transfer only"] = ([Family(10, graphics: false, compute: false, present: true)], false, -1),
        ["compute without graphics"] = ([Family(10, graphics: false, compute: true, present: true)], false, -1),
        ["one graphics family without compute"] = ([Family(10, graphics: true, compute: false, present: false)], false, 10),
        ["compute preferred over an earlier graphics family"] = (
            [Family(10, graphics: true, compute: false, present: true), Family(11, graphics: true, compute: true, present: true)], false, 11),
        ["first of two compute families"] = (
            [Family(10, graphics: true, compute: true, present: true), Family(11, graphics: true, compute: true, present: true)], false, 10),
        ["first of three compute families after a graphics one"] = (
            [
                Family(10, graphics: true, compute: false, present: true),
                Family(11, graphics: true, compute: true, present: true),
                Family(12, graphics: true, compute: true, present: true),
                Family(13, graphics: true, compute: true, present: true),
            ], false, 11),
        ["fallback to the first of two graphics families"] = (
            [Family(10, graphics: true, compute: false, present: true), Family(11, graphics: true, compute: false, present: true)], false, 10),
        ["fallback after a compute family without graphics"] = (
            [
                Family(10, graphics: false, compute: true, present: true),
                Family(11, graphics: true, compute: false, present: true),
                Family(12, graphics: true, compute: false, present: true),
            ], false, 11),
        ["compute family after a compute-only and a graphics family"] = (
            [
                Family(10, graphics: false, compute: true, present: true),
                Family(11, graphics: true, compute: false, present: true),
                Family(12, graphics: true, compute: true, present: true),
            ], false, 12),
        ["present filter skips a compute family that can't present"] = (
            [Family(10, graphics: true, compute: true, present: false), Family(11, graphics: true, compute: false, present: true)], true, 11),
        ["present filter skips a graphics family that can't present"] = (
            [Family(10, graphics: true, compute: false, present: false), Family(11, graphics: true, compute: false, present: true)], true, 11),
        ["present filter, compute preferred among presenting families"] = (
            [
                Family(10, graphics: true, compute: true, present: false),
                Family(11, graphics: true, compute: false, present: true),
                Family(12, graphics: true, compute: true, present: true),
            ], true, 12),
        ["present filter, no family presents"] = (
            [Family(10, graphics: true, compute: true, present: false), Family(11, graphics: true, compute: false, present: false)], true, -1),
        ["present filter, graphics and present on different families"] = (
            [Family(10, graphics: false, compute: true, present: true), Family(11, graphics: true, compute: true, present: false)], true, -1),
        ["no present needed, a family that can't present passes"] = (
            [Family(10, graphics: true, compute: true, present: false)], false, 10),
        ["no present needed, compute preferred over a presenting family"] = (
            [Family(10, graphics: true, compute: false, present: true), Family(11, graphics: true, compute: true, present: false)], false, 11),
    };

    public static TheoryData<string> Cases => [.. _cases.Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Choose_Cases(string name)
    {
        (AdapterCandidate.QueueFamily[] families, bool needsPresent, int expected) = _cases[name];

        Assert.Equal(expected, QueueFamilyChoice.Choose(families, needsPresent));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ChoosePosition_Cases_PositionOfTheChosenIndex(string name)
    {
        (AdapterCandidate.QueueFamily[] families, bool needsPresent, int expected) = _cases[name];

        Assert.Equal(expected < 0 ? -1 : expected - 10, QueueFamilyChoice.ChoosePosition(families, needsPresent));
    }

    [Fact]
    public void Choose_IndexDiffersFromPosition_ReturnsTheIndex()
    {
        AdapterCandidate.QueueFamily[] families =
            [Family(7, graphics: false, compute: false, present: false), Family(3, graphics: true, compute: true, present: true)];

        Assert.Equal(3, QueueFamilyChoice.Choose(families, needsPresent: true));
        Assert.Equal(1, QueueFamilyChoice.ChoosePosition(families, needsPresent: true));
    }
}
