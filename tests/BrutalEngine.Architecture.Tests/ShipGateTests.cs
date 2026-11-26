using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrutalEngine.Architecture.Tests.Fakes;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

/// <summary>ADR-0016: a library packs only when its milestone is released, and every library declares its milestone.</summary>
public sealed class ShipGateTests
{
    [Fact]
    public void EngineLibraries_EachDeclareOneIntegerMilestone()
    {
        ShipGateRule.Report report = CheckRepository();

        // Guards against a discovery bug making the check pass vacuously: Core always exists.
        Assert.Contains("BrutalEngine.Core", report.Milestones.Keys);
        Assert.Equal(Repository.EngineSourceProjects().Count, report.Milestones.Count + report.MilestoneViolations.Count);

        Violations.AssertNone(ShipGateRule.Name, report.MilestoneViolations);
    }

    [Fact]
    public void EngineLibraries_NeverSetIsPackable()
    {
        ShipGateRule.Report report = CheckRepository();

        Assert.Contains("BrutalEngine.Core", report.Milestones.Keys);
        Violations.AssertNone(ShipGateRule.Name, report.IsPackableViolations);
    }

    [Fact]
    public void EngineLibraries_NeverSetReleaseMilestone()
    {
        ShipGateRule.Report report = CheckRepository();

        Assert.Contains("BrutalEngine.Core", report.Milestones.Keys);
        Violations.AssertNone(ShipGateRule.Name, report.ReleaseMilestoneViolations);
    }

    [Fact]
    public void Expected_ReleaseMilestone1_IsTheSixM0AndM1Libraries()
    {
        // Libraries of milestone 0 or 1 created later (Runtime, DebugOverlay) join this list in the PR that adds them.
        Assert.Equal(
            [
                "BrutalEngine.Core",
                "BrutalEngine.Graphics",
                "BrutalEngine.Graphics.Vulkan",
                "BrutalEngine.Platform",
                "BrutalEngine.Platform.Sdl3",
                "BrutalEngine.World",
            ],
            CheckRepository().Expected(1));
    }

    [Fact]
    public void Expected_ReleaseMilestone0_IsCoreOnly()
    {
        Assert.Equal(["BrutalEngine.Core"], CheckRepository().Expected(0));
    }

    [Fact]
    public void ReadReleaseMilestone_SrcBuildProps_DeclaresOneInteger()
    {
        string props = Path.Combine(Repository.Root(), "src", "Directory.Build.props");

        // Only its presence and form are pinned: the release PR after each tag raises the value (ADR-0016).
        Assert.InRange(ShipGateRule.ReadReleaseMilestone(props, File.ReadAllText(props)), 1, int.MaxValue);
    }

    [Fact]
    public void Expected_FixtureMilestone2Library_PacksOnlyFromRelease2()
    {
        ShipGateRule.Report report = CheckFixtures(
            ("Fixture.Core", ShipGateFixtures.WithMilestone("0")),
            ("Fixture.Next", ShipGateFixtures.WithMilestone("2")),
            ("Fixture.Platform", ShipGateFixtures.WithMilestone("1")));

        Assert.Empty(report.MilestoneViolations);
        Assert.Equal(["Fixture.Core"], report.Expected(0));
        Assert.Equal(["Fixture.Core", "Fixture.Platform"], report.Expected(1));
        Assert.Equal(["Fixture.Core", "Fixture.Next", "Fixture.Platform"], report.Expected(2));
        Assert.Equal(["Fixture.Core", "Fixture.Next", "Fixture.Platform"], report.Expected(3));
    }

    [Theory]
    [InlineData("missing", "declares no BrutalEngineMilestone")]
    [InlineData("empty", "isn't a non-negative integer")]
    [InlineData("word", "isn't a non-negative integer")]
    [InlineData("negative", "isn't a non-negative integer")]
    [InlineData("fraction", "isn't a non-negative integer")]
    [InlineData("padded", "isn't a non-negative integer")]
    [InlineData("ten digits", "isn't a non-negative integer")]
    [InlineData("twice", "declares BrutalEngineMilestone 2 times")]
    [InlineData("conditional group", "sets BrutalEngineMilestone under a condition")]
    [InlineData("conditional element", "sets BrutalEngineMilestone under a condition")]
    [InlineData("not xml", "isn't a readable project file")]
    [InlineData("mixed-case duplicate", "spells BrutalEngineMilestone as \"brutalEngineMilestone\"")]
    [InlineData("upper case", "spells BrutalEngineMilestone as \"BRUTALENGINEMILESTONE\"")]
    public void Check_MissingOrNonIntegerMilestone_ReportsTheCsproj(string fixture, string expected)
    {
        string text = fixture switch
        {
            "missing" => ShipGateFixtures.Library("<PackageTags>gamedev</PackageTags>"),
            "empty" => ShipGateFixtures.WithMilestone(""),
            "word" => ShipGateFixtures.WithMilestone("two"),
            "negative" => ShipGateFixtures.WithMilestone("-1"),
            "fraction" => ShipGateFixtures.WithMilestone("1.5"),
            "padded" => ShipGateFixtures.WithMilestone(" 1"),
            "ten digits" => ShipGateFixtures.WithMilestone("1000000000"),
            "twice" => ShipGateFixtures.TwoMilestones,
            "conditional group" => ShipGateFixtures.ConditionalMilestone,
            "conditional element" => ShipGateFixtures.ConditionalMilestoneElement,
            "not xml" => ShipGateFixtures.NotXml,
            "mixed-case duplicate" => ShipGateFixtures.MixedCaseMilestones,
            "upper case" => ShipGateFixtures.UpperCaseMilestone,
            _ => throw new ArgumentOutOfRangeException(nameof(fixture), fixture, null),
        };

        ShipGateRule.Report report = CheckFixtures(("Fixture.Valid", ShipGateFixtures.WithMilestone("1")), ("Fixture.Broken", text));

        string violation = Assert.Single(report.MilestoneViolations);
        Assert.StartsWith($"Fixture.Broken ({FixturePath("Fixture.Broken")}): ", violation, StringComparison.Ordinal);
        Assert.Contains(expected, violation, StringComparison.Ordinal);
        Assert.Contains("the milestone that first ships this library; see ADR-0016", violation, StringComparison.Ordinal);
        Assert.Equal(["Fixture.Valid"], report.Milestones.Keys);
        Assert.Empty(report.IsPackableViolations);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("7", 7)]
    [InlineData("007", 7)]
    [InlineData("08", 8)]
    [InlineData("999999999", 999999999)]
    public void Check_IntegerMilestone_IsRead(string milestone, int expected)
    {
        ShipGateRule.Report report = CheckFixtures(("Fixture.Lib", ShipGateFixtures.WithMilestone(milestone)));

        Assert.Empty(report.MilestoneViolations);
        Assert.Equal(expected, report.Milestones["Fixture.Lib"]);
    }

    [Fact]
    public void Check_ProjectSettingIsPackable_ReportsTheCsproj()
    {
        ShipGateRule.Report report = CheckFixtures(
            ("Fixture.Clean", ShipGateFixtures.WithMilestone("1")),
            ("Fixture.Packable", ShipGateFixtures.SettingIsPackable));

        string violation = Assert.Single(report.IsPackableViolations);
        Assert.StartsWith($"Fixture.Packable ({FixturePath("Fixture.Packable")}): sets IsPackable.", violation, StringComparison.Ordinal);
        Assert.Contains("src/Directory.Build.targets", violation, StringComparison.Ordinal);
        Assert.Empty(report.MilestoneViolations);
    }

    [Theory]
    [InlineData("isPackable", false)]
    [InlineData("ISPACKABLE", false)]
    [InlineData("isPackable", true)]
    public void Check_ProjectSettingIsPackableInAnyCase_ReportsTheCsproj(string elementName, bool conditioned)
    {
        // MSBuild property names are case-insensitive, so each of these sets IsPackable.
        ShipGateRule.Report report = CheckFixtures(
            ("Fixture.Clean", ShipGateFixtures.WithMilestone("1")),
            ("Fixture.Packable", ShipGateFixtures.SettingIsPackableAs(elementName, conditioned)));

        string violation = Assert.Single(report.IsPackableViolations);
        Assert.StartsWith($"Fixture.Packable ({FixturePath("Fixture.Packable")}): sets IsPackable.", violation, StringComparison.Ordinal);
        Assert.Empty(report.MilestoneViolations);
    }

    [Theory]
    [InlineData("BrutalEngineReleaseMilestone", false)]
    [InlineData("brutalEngineReleaseMilestone", false)]
    [InlineData("BrutalEngineReleaseMilestone", true)]
    public void Check_ProjectSettingReleaseMilestone_ReportsTheCsproj(string elementName, bool conditioned)
    {
        // Each of these overrides the release milestone for this library alone, so it would silently stop packing.
        ShipGateRule.Report report = CheckFixtures(
            ("Fixture.Clean", ShipGateFixtures.WithMilestone("1")),
            ("Fixture.Unpacked", ShipGateFixtures.SettingReleaseMilestoneAs(elementName, conditioned)));

        string violation = Assert.Single(report.ReleaseMilestoneViolations);
        Assert.StartsWith(
            $"Fixture.Unpacked ({FixturePath("Fixture.Unpacked")}): sets BrutalEngineReleaseMilestone.", violation, StringComparison.Ordinal);
        Assert.Contains("src/Directory.Build.props", violation, StringComparison.Ordinal);
        Assert.Empty(report.MilestoneViolations);
        Assert.Empty(report.IsPackableViolations);
        Assert.Equal(1, report.Milestones["Fixture.Unpacked"]);
    }

    [Fact]
    public void Expected_WithMilestoneViolations_Throws()
    {
        ShipGateRule.Report report = CheckFixtures(("Fixture.Broken", ShipGateFixtures.WithMilestone("x")));

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => report.Expected(1));

        Assert.Contains("Fixture.Broken", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Expected_NegativeReleaseMilestone_Throws()
    {
        ShipGateRule.Report report = CheckFixtures(("Fixture.Lib", ShipGateFixtures.WithMilestone("0")));

        Assert.Throws<ArgumentOutOfRangeException>(() => report.Expected(-1));
    }

    [Theory]
    [InlineData("<BrutalEngineReleaseMilestone>0</BrutalEngineReleaseMilestone>", 0)]
    [InlineData("<BrutalEngineReleaseMilestone>2</BrutalEngineReleaseMilestone>", 2)]
    public void ReadReleaseMilestone_OneInteger_ReturnsIt(string properties, int expected)
    {
        Assert.Equal(expected, ShipGateRule.ReadReleaseMilestone(FixturePath("Directory.Build"), ShipGateFixtures.Library(properties)));
    }

    [Theory]
    [InlineData("<PackageTags>gamedev</PackageTags>")]
    [InlineData("<BrutalEngineReleaseMilestone>one</BrutalEngineReleaseMilestone>")]
    [InlineData("<BrutalEngineReleaseMilestone>1</BrutalEngineReleaseMilestone><BrutalEngineReleaseMilestone>2</BrutalEngineReleaseMilestone>")]
    [InlineData("<BrutalEngineReleaseMilestone Condition=\"true\">1</BrutalEngineReleaseMilestone>")]
    public void ReadReleaseMilestone_MissingOrInvalid_ThrowsNamingTheFile(string properties)
    {
        string path = FixturePath("Directory.Build");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ShipGateRule.ReadReleaseMilestone(path, ShipGateFixtures.Library(properties)));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.Contains(ShipGateRule.ReleaseMilestoneProperty, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadReleaseMilestone_NonCanonicalSpelling_ThrowsNamingTheSpelling()
    {
        string path = FixturePath("Directory.Build");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => ShipGateRule.ReadReleaseMilestone(
                path, ShipGateFixtures.Library("<brutalEngineReleaseMilestone>1</brutalEngineReleaseMilestone>")));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.Contains("spells BrutalEngineReleaseMilestone as \"brutalEngineReleaseMilestone\"", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadReleaseMilestone_NotXml_ThrowsNamingTheFile()
    {
        string path = FixturePath("Directory.Build");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => ShipGateRule.ReadReleaseMilestone(path, ShipGateFixtures.NotXml));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    private static ShipGateRule.Report CheckRepository() =>
        ShipGateRule.Check(Repository.EngineSourceProjects(), File.ReadAllText);

    private static ShipGateRule.Report CheckFixtures(params (string Name, string Text)[] projects)
    {
        Dictionary<string, string> texts = projects.ToDictionary(p => FixturePath(p.Name), p => p.Text, StringComparer.Ordinal);
        return ShipGateRule.Check(projects.Select(p => new ProjectFile(p.Name, FixturePath(p.Name))), path => texts[path]);
    }

    private static string FixturePath(string name) =>
        Path.Combine(Path.GetPathRoot(Repository.Root())!, "repo", "src", name, name + ".csproj");
}
