using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BrutalEngine.Architecture.Tests.Fakes;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

/// <summary>ADR-0016: an engine library never references a library that a later milestone ships.</summary>
public sealed class MilestoneReferenceTests
{
    [Fact]
    public void EngineLibraries_ReferenceNoLaterMilestone()
    {
        MilestoneReferenceRule.Report report = MilestoneReferenceRule.Check(Repository.EngineSourceProjects(), File.ReadAllText);

        // Guards against a discovery or parsing bug making the check pass vacuously: Graphics always references Core.
        Assert.Contains("BrutalEngine.Graphics -> BrutalEngine.Core", report.Checked);
        Violations.AssertNone(MilestoneReferenceRule.Name, report.Violations);
    }

    [Fact]
    public void Check_ReferenceToLaterMilestone_ReportsBothProjects()
    {
        MilestoneReferenceRule.Report report = Check(
            ("Fixture.Core", MilestoneReferenceFixtures.Library("0")),
            ("Fixture.Runtime", MilestoneReferenceFixtures.Library("1", "Fixture.Core", "Fixture.Input")),
            ("Fixture.Input", MilestoneReferenceFixtures.Library("2", "Fixture.Core")));

        string violation = Assert.Single(report.Violations);
        Assert.StartsWith(
            "Fixture.Runtime (milestone 1) references Fixture.Input (milestone 2): "
            + "a library may reference only libraries of its own or an earlier milestone.",
            violation,
            StringComparison.Ordinal);
        Assert.Contains("ADR-0016", violation, StringComparison.Ordinal);
        Assert.Equal(
            ["Fixture.Runtime -> Fixture.Core", "Fixture.Runtime -> Fixture.Input", "Fixture.Input -> Fixture.Core"],
            report.Checked);
    }

    [Fact]
    public void Check_ReferenceToSameMilestone_Passes()
    {
        MilestoneReferenceRule.Report report = Check(
            ("Fixture.Platform", MilestoneReferenceFixtures.Library("1")),
            ("Fixture.Graphics", MilestoneReferenceFixtures.Library("1", "Fixture.Platform")));

        Assert.Empty(report.Violations);
        Assert.Equal(["Fixture.Graphics -> Fixture.Platform"], report.Checked);
    }

    [Fact]
    public void Check_ReferenceToEarlierMilestone_Passes()
    {
        MilestoneReferenceRule.Report report = Check(
            ("Fixture.Platform", MilestoneReferenceFixtures.Library("1")),
            ("Fixture.Input", MilestoneReferenceFixtures.Library("2", "Fixture.Platform")));

        Assert.Empty(report.Violations);
        Assert.Equal(["Fixture.Input -> Fixture.Platform"], report.Checked);
    }

    [Fact]
    public void Check_ReferenceToRoslynComponent_IsIgnored()
    {
        // A Roslyn component is build-time only and never a package dependency, so a milestone on one means nothing.
        MilestoneReferenceRule.Report report = Check(
            ("Fixture.World", MilestoneReferenceFixtures.Library("1", "Fixture.World.Generators", "Fixture.Analyzers")),
            ("Fixture.World.Generators", MilestoneReferenceFixtures.Library("2")),
            ("Fixture.Analyzers", MilestoneReferenceFixtures.Library("2")));

        Assert.Empty(report.Violations);
        Assert.Empty(report.Checked);
    }

    [Fact]
    public void Check_RoslynComponentReferencingLaterMilestone_IsIgnored()
    {
        MilestoneReferenceRule.Report report = Check(
            ("Fixture.World.Generators", MilestoneReferenceFixtures.Library("1", "Fixture.Next")),
            ("Fixture.Next", MilestoneReferenceFixtures.Library("2")));

        Assert.Empty(report.Violations);
        Assert.Empty(report.Checked);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("word")]
    [InlineData("conditional")]
    [InlineData("upper case")]
    [InlineData("twice")]
    [InlineData("not xml")]
    public void Check_LibraryWithoutMilestone_IsNotReported(string fixture)
    {
        string? properties = fixture switch
        {
            "missing" => "<PackageTags>gamedev</PackageTags>",
            "word" => "<BrutalEngineMilestone>one</BrutalEngineMilestone>",
            "conditional" => "<BrutalEngineMilestone Condition=\"'$(Configuration)' == 'Release'\">1</BrutalEngineMilestone>",
            "upper case" => "<BRUTALENGINEMILESTONE>1</BRUTALENGINEMILESTONE>",
            "twice" => "<BrutalEngineMilestone>1</BrutalEngineMilestone><BrutalEngineMilestone>1</BrutalEngineMilestone>",
            "not xml" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(fixture), fixture, null),
        };
        string broken = properties is null
            ? ShipGateFixtures.NotXml
            : MilestoneReferenceFixtures.WithProperties(properties, "Fixture.Next");

        // Read loosely, the broken library's milestone is 1, which would break the rule on both sides: a milestone-0
        // library references it, and it references a milestone-2 library. The ship-gate rule reports its milestone.
        MilestoneReferenceRule.Report report = Check(
            ("Fixture.Early", MilestoneReferenceFixtures.Library("0", "Fixture.Broken")),
            ("Fixture.Broken", broken),
            ("Fixture.Next", MilestoneReferenceFixtures.Library("2")));

        Assert.Empty(report.Violations);
        Assert.Empty(report.Checked);
    }

    [Theory]
    [InlineData("08", "9", "Fixture.From (milestone 8) references Fixture.To (milestone 9): ")]
    [InlineData("9", "08", null)]
    [InlineData("9", "010", "Fixture.From (milestone 9) references Fixture.To (milestone 10): ")]
    [InlineData("010", "9", null)]
    public void Check_LeadingZeroMilestones_CompareAsDecimal(string from, string to, string? expected)
    {
        // As text, "08" sorts before "9" and "010" before "9"; as octal, "08" isn't a number at all.
        MilestoneReferenceRule.Report report = Check(
            ("Fixture.From", MilestoneReferenceFixtures.Library(from, "Fixture.To")),
            ("Fixture.To", MilestoneReferenceFixtures.Library(to)));

        Assert.Equal(["Fixture.From -> Fixture.To"], report.Checked);
        if (expected is null)
        {
            Assert.Empty(report.Violations);
        }
        else
        {
            Assert.StartsWith(expected, Assert.Single(report.Violations), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("""<ProjectReference Include="..\Fixture.Next\Fixture.Next.csproj" />""")]
    [InlineData("""<ProjectReference Include="Fixture.Next.csproj" />""")]
    [InlineData("""<ProjectReference Include="../Fixture.Core/Fixture.Core.csproj; ../Fixture.Next/Fixture.Next.csproj ;" />""")]
    [InlineData("""<projectreference Include="../Fixture.Next/Fixture.Next.csproj" />""")]
    [InlineData("""<ProjectReference Include="../Fixture.Next/Fixture.Next.csproj" Condition="'$(Configuration)' == 'Release'" />""")]
    [InlineData("""<ProjectReference Include="$(MSBuildThisFileDirectory)../Fixture.Next/Fixture.Next.csproj" />""")]
    public void Check_ReferenceInAnyMSBuildForm_IsRead(string item)
    {
        // MSBuild accepts either path separator, splits Include on semicolons, matches item names in any case, and
        // includes a conditional item in some build.
        MilestoneReferenceRule.Report report = Check(
            ("Fixture.Core", MilestoneReferenceFixtures.Library("0")),
            ("Fixture.Lib", MilestoneReferenceFixtures.Milestone1WithItems(item)),
            ("Fixture.Next", MilestoneReferenceFixtures.Library("2")));

        string violation = Assert.Single(report.Violations);
        Assert.StartsWith("Fixture.Lib (milestone 1) references Fixture.Next (milestone 2): ", violation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("$(NextProject)")]
    [InlineData("../Fixture.Next/$(NextName).csproj")]
    [InlineData("@(LaterProjects)")]
    [InlineData("../Fixture.Next/%(Filename).csproj")]
    [InlineData("../*/*.csproj")]
    [InlineData("../Fixture.Nex?/Fixture.Nex?.csproj")]
    public void Check_ReferenceWithoutLiteralProjectName_IsReported(string include)
    {
        MilestoneReferenceRule.Report report = Check(
            ("Fixture.Lib", MilestoneReferenceFixtures.Milestone1WithItems($"<ProjectReference Include=\"{include}\" />")),
            ("Fixture.Next", MilestoneReferenceFixtures.Library("2")));

        string violation = Assert.Single(report.Violations);
        Assert.StartsWith(
            $"Fixture.Lib (milestone 1) has the ProjectReference \"{include}\", whose project name isn't written out",
            violation,
            StringComparison.Ordinal);
        Assert.Empty(report.Checked);
    }

    [Fact]
    public void Check_ReferenceToProjectOutsideTheLibraries_IsIgnored()
    {
        MilestoneReferenceRule.Report report = Check(
            ("Fixture.Lib", MilestoneReferenceFixtures.Library("1", "Fixture.Cook")),
            ("Fixture.Next", MilestoneReferenceFixtures.Library("2")));

        Assert.Empty(report.Violations);
        Assert.Empty(report.Checked);
    }

    [Fact]
    public void Check_SeveralLaterReferences_ReportsEachOnce()
    {
        MilestoneReferenceRule.Report report = Check(
            ("Fixture.Core", MilestoneReferenceFixtures.Library("0", "Fixture.Platform", "Fixture.Next", "Fixture.Platform")),
            ("Fixture.Platform", MilestoneReferenceFixtures.Library("1", "Fixture.Next")),
            ("Fixture.Next", MilestoneReferenceFixtures.Library("2")));

        Assert.Collection(
            report.Violations,
            v => Assert.StartsWith("Fixture.Core (milestone 0) references Fixture.Platform (milestone 1): ", v, StringComparison.Ordinal),
            v => Assert.StartsWith("Fixture.Core (milestone 0) references Fixture.Next (milestone 2): ", v, StringComparison.Ordinal),
            v => Assert.StartsWith("Fixture.Platform (milestone 1) references Fixture.Next (milestone 2): ", v, StringComparison.Ordinal));
        Assert.Equal(
            ["Fixture.Core -> Fixture.Platform", "Fixture.Core -> Fixture.Next", "Fixture.Platform -> Fixture.Next"],
            report.Checked);
    }

    [Fact]
    public void Check_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => MilestoneReferenceRule.Check(null!, File.ReadAllText));
        Assert.Throws<ArgumentNullException>(() => MilestoneReferenceRule.Check([], null!));
    }

    private static MilestoneReferenceRule.Report Check(params (string Name, string Text)[] projects)
    {
        Dictionary<string, string> texts = projects.ToDictionary(p => FixturePath(p.Name), p => p.Text, StringComparer.Ordinal);
        return MilestoneReferenceRule.Check(projects.Select(p => new ProjectFile(p.Name, FixturePath(p.Name))), path => texts[path]);
    }

    private static string FixturePath(string name) =>
        Path.Combine(Path.GetPathRoot(Repository.Root())!, "repo", "src", name, name + ".csproj");
}
