using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace BrutalEngine.Architecture.Tests;

/// <summary>A project file in the repository, named by its file name (= assembly name).</summary>
internal sealed record ProjectFile(string Name, string Path)
{
    /// <summary>The project's ProjectReference items, as full paths.</summary>
    public IReadOnlyList<string> ProjectReferences()
    {
        string directory = System.IO.Path.GetDirectoryName(Path)!;
        return
        [
            .. XDocument.Load(Path)
                .Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => (string?)e.Attribute("Include"))
                .OfType<string>()
                .Select(include => System.IO.Path.GetFullPath(System.IO.Path.Combine(directory, include))),
        ];
    }
}

/// <summary>Finds the repository and the engine projects in it.</summary>
internal static class Repository
{
    public const string SolutionFileName = "BrutalEngine.slnx";

    public static string Root()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException(
            $"No {SolutionFileName} found above {AppContext.BaseDirectory}. The architecture tests must run from a build inside the repository.");
    }

    /// <summary>Every src/*/*.csproj except Roslyn components: the engine libraries the rules apply to.</summary>
    public static IReadOnlyList<ProjectFile> EngineSourceProjects()
    {
        string src = Path.Combine(Root(), "src");
        return
        [
            .. Directory.EnumerateDirectories(src)
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.csproj"))
                .Select(path => new ProjectFile(Path.GetFileNameWithoutExtension(path), Path.GetFullPath(path)))
                .Where(project => !LayerMap.IsRoslynComponentName(project.Name))
                .OrderBy(project => project.Name, StringComparer.Ordinal),
        ];
    }

    public static ProjectFile ArchitectureTestProject()
    {
        const string Name = "BrutalEngine.Architecture.Tests";
        return new ProjectFile(Name, Path.GetFullPath(Path.Combine(Root(), "tests", Name, Name + ".csproj")));
    }

    public static string ReadDoc(string relativePath) => File.ReadAllText(Path.Combine(Root(), "docs", relativePath));
}
