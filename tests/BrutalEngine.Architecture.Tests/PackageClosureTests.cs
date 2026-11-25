using System;
using System.Collections.Generic;
using System.IO;
using BrutalEngine.Architecture.Tests.Fakes;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

/// <summary>ADR-0011: Core, Platform, Graphics and World never resolve the package that brings the ObjectPool dependency.</summary>
public sealed class PackageClosureTests
{
    private const string Module = "BrutalEngine.Core";

    [Fact]
    public void GuardedModuleLockFiles_ResolveNoBannedPackage()
    {
        PackageClosureRule.Report report = PackageClosureRule.Check(
            Repository.EngineSourceProjects(),
            path => File.Exists(path) ? File.ReadAllText(path) : null);

        // Guards against a discovery bug making the check pass vacuously: Core always exists.
        Assert.Contains(Module, report.Checked);
        if (report.NotCreated.Count != 0)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"Not created yet (checked as soon as their project exists): {string.Join(", ", report.NotCreated)}");
        }

        Violations.AssertNone(PackageClosureRule.Name, report.Violations);
    }

    [Fact]
    public void GuardedModules_AreExactlyCorePlatformGraphicsWorld()
    {
        Assert.Equal(
            ["BrutalEngine.Core", "BrutalEngine.Platform", "BrutalEngine.Graphics", "BrutalEngine.World"],
            PackageClosureRule.GuardedModules);

        // Each name is a planned engine library, so a typo can't guard nothing.
        foreach (string module in PackageClosureRule.GuardedModules)
        {
            Assert.True(EngineLayers.Map.TryGet(module, out ModuleDefinition definition), module);
            Assert.Equal(ModuleKind.Library, definition.Kind);
        }
    }

    [Fact]
    public void BannedPackages_AreStringsAndObjectPool()
    {
        Assert.Equal(["Ahwoo.Brutal.Strings", "Microsoft.Extensions.ObjectPool"], PackageClosureRule.BannedPackages);
    }

    [Fact]
    public void FindBannedPackages_LockFileWithStrings_ReportsIt()
    {
        IReadOnlyList<string> violations = PackageClosureRule.FindBannedPackages(Module, LockFileFixtures.WithStrings);

        string violation = Assert.Single(violations);
        Assert.StartsWith($"{Module} resolves Ahwoo.Brutal.Strings 2026.10.2 in net11.0.", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void FindBannedPackages_LockFileWithObjectPoolInRidSection_ReportsIt()
    {
        IReadOnlyList<string> violations = PackageClosureRule.FindBannedPackages(Module, LockFileFixtures.WithObjectPoolInRidSection);

        string violation = Assert.Single(violations);
        Assert.StartsWith($"{Module} resolves Microsoft.Extensions.ObjectPool 11.0.0 in net11.0/win-x64.", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void FindBannedPackages_LockFileWithOnlyBrutalMemory_ReportsNothing()
    {
        Assert.Empty(PackageClosureRule.FindBannedPackages(Module, LockFileFixtures.WithOnlyBrutalMemory));
    }

    [Fact]
    public void FindBannedPackages_BothPackagesInSeveralSections_ReportsOneLinePerPackage()
    {
        IReadOnlyList<string> violations = PackageClosureRule.FindBannedPackages(Module, LockFileFixtures.WithBothInSeveralSections);

        Assert.Collection(
            violations,
            v => Assert.StartsWith($"{Module} resolves Ahwoo.Brutal.Strings 2026.10.2 in net11.0, net11.0/linux-x64.", v, StringComparison.Ordinal),
            v => Assert.StartsWith($"{Module} resolves Microsoft.Extensions.ObjectPool 11.0.0 in net11.0, net11.0/linux-x64.", v, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(LockFileFixtures.WithoutDependencies)]
    [InlineData("""{ "version": 2, "dependencies": { "net11.0": [] } }""")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void FindBannedPackages_NotALockFile_Throws(string json)
    {
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => PackageClosureRule.FindBannedPackages(Module, json));

        Assert.Contains(Module, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FindBannedPackages_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => PackageClosureRule.FindBannedPackages(null!, LockFileFixtures.WithStrings));
        Assert.Throws<ArgumentNullException>(() => PackageClosureRule.FindBannedPackages(Module, null!));
    }

    [Fact]
    public void Check_GuardedProjectWithBannedPackage_ReportsIt()
    {
        ProjectFile graphics = FakeProject("BrutalEngine.Graphics");

        PackageClosureRule.Report report = PackageClosureRule.Check([graphics], ReadFrom(graphics, LockFileFixtures.WithStrings));

        Assert.Equal(["BrutalEngine.Graphics"], report.Checked);
        string violation = Assert.Single(report.Violations);
        Assert.StartsWith("BrutalEngine.Graphics resolves Ahwoo.Brutal.Strings", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_GuardedProjectWithoutLockFile_ReportsIt()
    {
        ProjectFile world = FakeProject("BrutalEngine.World");

        PackageClosureRule.Report report = PackageClosureRule.Check([world], _ => null);

        string violation = Assert.Single(report.Violations);
        Assert.StartsWith($"BrutalEngine.World has no {PackageClosureRule.LockFileName} next to {world.Path}.", violation, StringComparison.Ordinal);
        Assert.Empty(report.Checked);
    }

    [Fact]
    public void Check_GuardedModulesWithoutProject_AreReportedAsNotCreated()
    {
        ProjectFile core = FakeProject("BrutalEngine.Core");

        PackageClosureRule.Report report = PackageClosureRule.Check([core], ReadFrom(core, LockFileFixtures.WithOnlyBrutalMemory));

        Assert.Equal(["BrutalEngine.Core"], report.Checked);
        Assert.Empty(report.Violations);
        Assert.Equal(["BrutalEngine.Platform", "BrutalEngine.Graphics", "BrutalEngine.World"], report.NotCreated);
    }

    [Fact]
    public void Check_UnguardedProjectWithBannedPackage_IsIgnored()
    {
        ProjectFile backend = FakeProject("BrutalEngine.Graphics.Vulkan");

        PackageClosureRule.Report report = PackageClosureRule.Check([backend], ReadFrom(backend, LockFileFixtures.WithStrings));

        Assert.Empty(report.Checked);
        Assert.Empty(report.Violations);
    }

    private static ProjectFile FakeProject(string name) =>
        new(name, Path.Combine(Path.GetPathRoot(Repository.Root())!, "repo", "src", name, name + ".csproj"));

    /// <summary>Returns <paramref name="json"/> for the project's lock file path and null (no file) for any other path.</summary>
    private static Func<string, string?> ReadFrom(ProjectFile project, string json)
    {
        string lockFile = Path.Combine(Path.GetDirectoryName(project.Path)!, PackageClosureRule.LockFileName);
        return path => path == lockFile ? json : null;
    }
}
