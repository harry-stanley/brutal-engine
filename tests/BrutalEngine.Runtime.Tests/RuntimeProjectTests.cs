using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BrutalEngine.Architecture.Tests;
using Xunit;

namespace BrutalEngine.Runtime.Tests;

public sealed class RuntimeProjectTests
{
    private const string ProjectName = "BrutalEngine.Runtime";

    [Fact]
    public void Project_IsMilestoneOneHost()
    {
        // ADR-0016: an M1 library declares its milestone, which the ship gate packs against. Read from the csproj
        // itself, so a property inherited from somewhere else can't stand in for it.
        string[] milestones =
        [
            .. XDocument.Load(Path.Combine(RepositoryRoot(), "src", ProjectName, ProjectName + ".csproj"))
                .Descendants()
                .Where(e => e.Name.LocalName == "BrutalEngineMilestone")
                .Select(e => e.Value.Trim()),
        ];
        Assert.Equal(["1"], milestones);

        // The architecture tests' layer map places Runtime as the L5 host (docs/03-architecture.md 3.1), and the
        // runtime types live in the assembly that entry governs.
        Assert.True(EngineLayers.Map.TryGet(ProjectName, out ModuleDefinition module), "Runtime is not in the layer map.");
        Assert.Equal(5, module.Layer);
        Assert.Equal(ModuleKind.Host, module.Kind);
        Assert.Equal(ProjectName, typeof(FrameLoopOptions).Assembly.GetName().Name);
    }

    [Fact]
    public void FrameLoopOptions_Defaults_MatchTheDesign()
    {
        FrameLoopOptions options = new();

        Assert.Equal(60, options.FixedUpdateRate);
        Assert.Equal(TimeSpan.FromMilliseconds(250), options.MaxFrameDelta);
    }

    private static string RepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BrutalEngine.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("BrutalEngine.slnx not found above " + AppContext.BaseDirectory);
    }
}
