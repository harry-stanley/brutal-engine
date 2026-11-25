using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Xunit;

namespace BrutalEngine.Architecture.Tests;

/// <summary>
/// ADR-0014: every assembly the repository builds targets <c>net11.0</c>, except Roslyn components, which target
/// <c>netstandard2.0</c> so any host compiler can load them. The framework is set once, in the root
/// Directory.Build.props, so the static checks below cover the samples and test projects this project doesn't load.
/// </summary>
public sealed class TargetFrameworkTests
{
    private const string ExpectedTargetFramework = "net11.0";
    private const string ExpectedFrameworkName = ".NETCoreApp,Version=v11.0";
    private const string RoslynComponentTargetFramework = "netstandard2.0";
    private const int ExpectedRuntimeMajorVersion = 11;

    private static readonly string[] _frameworkProperties = ["TargetFramework", "TargetFrameworks"];

    [Fact]
    public void EngineAssemblies_TargetFrameworkAttribute_IsNet11()
    {
        List<Assembly> assemblies = [.. EngineModules.Load().Select(m => m.Assembly), typeof(TargetFrameworkTests).Assembly];
        List<string> problems = [];

        // Guards against an empty module list making the check pass vacuously.
        Assert.Contains(assemblies, a => a.GetName().Name == "BrutalEngine.Core");

        foreach (Assembly assembly in assemblies)
        {
            string? framework = assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
            if (framework != ExpectedFrameworkName)
            {
                problems.Add($"{assembly.GetName().Name}: built for {framework ?? "(no TargetFrameworkAttribute)"}, expected {ExpectedFrameworkName}.");
            }
        }

        Violations.AssertNone("every engine assembly targets " + ExpectedTargetFramework, problems);
    }

    [Fact]
    public void RootBuildProps_TargetFramework_IsNet11()
    {
        string path = Path.Combine(Repository.Root(), "Directory.Build.props");

        Assert.Equal([ExpectedTargetFramework], FrameworkValues(path));
    }

    [Fact]
    public void SolutionProjects_TargetFramework_InheritedExceptRoslynComponents()
    {
        IReadOnlyList<string> projects = SolutionProjectPaths();
        List<string> problems = [];

        // Guards against a parsing bug making the check pass vacuously.
        Assert.Contains(projects, p => Path.GetFileNameWithoutExtension(p) == "BrutalEngine.Core");
        Assert.Contains(projects, p => Path.GetFileNameWithoutExtension(p) == "HelloWindow");

        foreach (string project in projects)
        {
            string name = Path.GetFileNameWithoutExtension(project);
            string[] values = FrameworkValues(project);
            if (LayerMap.IsRoslynComponentName(name))
            {
                if (!values.SequenceEqual([RoslynComponentTargetFramework]))
                {
                    problems.Add($"{name}: Roslyn components set TargetFramework to {RoslynComponentTargetFramework}, found [{string.Join(", ", values)}].");
                }
            }
            else if (values.Length != 0)
            {
                problems.Add($"{name}: sets [{string.Join(", ", values)}]. Inherit {ExpectedTargetFramework} from the root Directory.Build.props.");
            }
        }

        string rootProps = Path.Combine(Repository.Root(), "Directory.Build.props");
        IReadOnlyList<string> buildFiles = BuildFiles();

        // Guards against the scan missing the root, where the one allowed TargetFramework lives.
        Assert.Contains(rootProps, buildFiles);

        foreach (string buildFile in buildFiles)
        {
            string[] values = FrameworkValues(buildFile);
            bool allowed = buildFile == rootProps ? values.SequenceEqual([ExpectedTargetFramework]) : values.Length == 0;
            if (!allowed)
            {
                problems.Add($"{Path.GetRelativePath(Repository.Root(), buildFile)}: sets [{string.Join(", ", values)}]. Only the root Directory.Build.props sets the target framework, once, to {ExpectedTargetFramework}.");
            }
        }

        Violations.AssertNone("the target framework is set only in the root Directory.Build.props", problems);
    }

    [Fact]
    public void TestHost_RuntimeVersion_IsDotNet11()
    {
        Assert.Equal(ExpectedRuntimeMajorVersion, Environment.Version.Major);
    }

    /// <summary>Every project in BrutalEngine.slnx, as full paths.</summary>
    private static IReadOnlyList<string> SolutionProjectPaths()
    {
        string root = Repository.Root();
        return
        [
            .. XDocument.Load(Path.Combine(root, Repository.SolutionFileName))
                .Descendants("Project")
                .Select(e => (string?)e.Attribute("Path"))
                .OfType<string>()
                .Select(path => Path.GetFullPath(Path.Combine(root, path))),
        ];
    }

    /// <summary>Every Directory.Build.props/.targets file in the repository, the root's included, as full paths.</summary>
    private static IReadOnlyList<string> BuildFiles()
    {
        string root = Repository.Root();
        string[] skipped = ["bin", "obj", ".git"];
        IEnumerable<string> nested = Directory.EnumerateDirectories(root)
            .Where(directory => !skipped.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
            .SelectMany(directory => Directory.EnumerateFiles(directory, "Directory.Build.*", SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj"));
        return
        [
            .. Directory.EnumerateFiles(root, "Directory.Build.*")
                .Concat(nested)
                .Where(path => Path.GetExtension(path) is ".props" or ".targets")
                .Select(Path.GetFullPath)
                .OrderBy(path => path, StringComparer.Ordinal),
        ];
    }

    /// <summary>The values of every TargetFramework or TargetFrameworks property set in an MSBuild file.</summary>
    private static string[] FrameworkValues(string path) =>
    [
        .. XDocument.Load(path)
            .Descendants()
            .Where(e => _frameworkProperties.Contains(e.Name.LocalName, StringComparer.Ordinal))
            .Select(e => e.Value.Trim()),
    ];
}
