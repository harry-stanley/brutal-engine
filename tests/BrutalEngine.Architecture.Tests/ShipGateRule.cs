using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace BrutalEngine.Architecture.Tests;

/// <summary>
/// ADR-0016's ship gate: every engine library's csproj declares <c>BrutalEngineMilestone</c>, the milestone that first
/// ships it, and never sets <c>IsPackable</c>, which src/Directory.Build.targets derives from the milestone. A library
/// packs only when its milestone is at most <c>BrutalEngineReleaseMilestone</c>, which only src/Directory.Build.props
/// sets: a csproj that set it would move the line for itself alone.
/// </summary>
/// <remarks>
/// A milestone is 1 to 9 decimal digits, set once, unconditionally. src/Directory.Build.targets and
/// .github/scripts/check-packages.sh accept the same values, so the three agree on what packs. MSBuild property names
/// are case-insensitive, so the rule matches names ignoring case, as MSBuild does, and reports a milestone spelled in
/// any other case: check-packages.sh reads the exact name, and would otherwise disagree with what MSBuild evaluates.
/// </remarks>
internal static partial class ShipGateRule
{
    public const string Name =
        "every engine library declares one integer BrutalEngineMilestone and never sets IsPackable or BrutalEngineReleaseMilestone";

    public const string MilestoneProperty = "BrutalEngineMilestone";

    public const string ReleaseMilestoneProperty = "BrutalEngineReleaseMilestone";

    private const string Hint = "Set it to the milestone that first ships this library; see ADR-0016.";

    /// <summary>
    /// Reads every project's milestone and finds the projects that break the rule. Each violation starts with
    /// "<c>name (path): </c>".
    /// </summary>
    /// <param name="projects">The engine projects, as <see cref="Repository.EngineSourceProjects"/> lists them.</param>
    /// <param name="readProject">Returns a project file's text from its full path.</param>
    public static Report Check(IEnumerable<ProjectFile> projects, Func<string, string> readProject)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(readProject);

        Dictionary<string, int> milestones = new(StringComparer.Ordinal);
        List<string> milestoneViolations = [];
        List<string> isPackableViolations = [];
        List<string> releaseMilestoneViolations = [];
        foreach (ProjectFile project in projects)
        {
            string prefix = $"{project.Name} ({project.Path}): ";
            XDocument document;
            try
            {
                document = XDocument.Parse(readProject(project.Path));
            }
            catch (XmlException exception)
            {
                milestoneViolations.Add($"{prefix}isn't a readable project file ({exception.Message}). {Hint}");
                continue;
            }

            if (Properties(document, "IsPackable").Count != 0)
            {
                isPackableViolations.Add(
                    $"{prefix}sets IsPackable. src/Directory.Build.targets derives it from {MilestoneProperty} (ADR-0016): remove it.");
            }

            if (Properties(document, ReleaseMilestoneProperty).Count != 0)
            {
                releaseMilestoneViolations.Add(
                    $"{prefix}sets {ReleaseMilestoneProperty}. Only src/Directory.Build.props sets it, for every library at once "
                    + $"(ADR-0016): remove it, and set {MilestoneProperty} to the milestone that first ships this library.");
            }

            string? problem = ReadMilestone(document, MilestoneProperty, out int milestone);
            if (problem is null)
            {
                milestones.Add(project.Name, milestone);
            }
            else
            {
                milestoneViolations.Add($"{prefix}{problem} {Hint}");
            }
        }

        return new Report(milestones, milestoneViolations, isPackableViolations, releaseMilestoneViolations);
    }

    /// <summary>Reads <c>BrutalEngineReleaseMilestone</c> from the text of src/Directory.Build.props.</summary>
    /// <exception cref="InvalidDataException">
    /// The text isn't XML, or doesn't set the property once, spelled exactly, to an integer, unconditionally.
    /// </exception>
    public static int ReadReleaseMilestone(string propsPath, string propsText)
    {
        ArgumentNullException.ThrowIfNull(propsPath);
        ArgumentNullException.ThrowIfNull(propsText);

        XDocument document;
        try
        {
            document = XDocument.Parse(propsText);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException($"{propsPath} isn't a readable MSBuild file, so {ReleaseMilestoneProperty} can't be read.", exception);
        }

        string? problem = ReadMilestone(document, ReleaseMilestoneProperty, out int milestone);
        return problem is null
            ? milestone
            : throw new InvalidDataException($"{propsPath}: {problem} It is the milestone the next release ships; see ADR-0016.");
    }

    /// <summary>Returns null and the value, or the reason the property isn't one unconditional integer.</summary>
    private static string? ReadMilestone(XDocument document, string property, out int milestone)
    {
        milestone = 0;
        List<XElement> elements = Properties(document, property);
        if (elements.Count == 0)
        {
            return $"declares no {property}.";
        }

        string[] spellings =
        [
            .. elements.Select(e => e.Name.LocalName).Where(name => name != property).Distinct(StringComparer.Ordinal),
        ];
        if (spellings.Length != 0)
        {
            return $"spells {property} as {string.Join(", ", spellings.Select(name => $"\"{name}\""))}; MSBuild reads any case, "
                + $"but .github/scripts/check-packages.sh reads only the exact name, so spell it {property}.";
        }

        if (elements.Count > 1)
        {
            return $"declares {property} {elements.Count} times; set it once.";
        }

        XElement element = elements[0];
        if (element.Attribute("Condition") is not null || element.Parent?.Attribute("Condition") is not null)
        {
            return $"sets {property} under a condition; set it unconditionally, so every build packs the same set.";
        }

        if (!MilestonePattern().IsMatch(element.Value))
        {
            return $"{property} \"{element.Value}\" isn't a non-negative integer (1 to 9 digits).";
        }

        milestone = int.Parse(element.Value, NumberStyles.None, CultureInfo.InvariantCulture);
        return null;
    }

    private static List<XElement> Properties(XDocument document, string property) =>
        [.. document.Descendants().Where(e => string.Equals(e.Name.LocalName, property, StringComparison.OrdinalIgnoreCase))];

    [GeneratedRegex("^[0-9]{1,9}$", RegexOptions.CultureInvariant)]
    private static partial Regex MilestonePattern();

    /// <summary>The result of <see cref="Check"/>.</summary>
    /// <param name="Milestones">The milestone of every project that declares a valid one, by project name.</param>
    /// <param name="MilestoneViolations">
    /// One line per project whose milestone is missing, spelled in another case, repeated, conditional or not an integer.
    /// </param>
    /// <param name="IsPackableViolations">One line per project that sets IsPackable itself, in any case.</param>
    /// <param name="ReleaseMilestoneViolations">
    /// One line per project that sets <c>BrutalEngineReleaseMilestone</c> itself, in any case or under any condition.
    /// </param>
    internal sealed record Report(
        IReadOnlyDictionary<string, int> Milestones,
        IReadOnlyList<string> MilestoneViolations,
        IReadOnlyList<string> IsPackableViolations,
        IReadOnlyList<string> ReleaseMilestoneViolations)
    {
        /// <summary>The projects that pack when <paramref name="releaseMilestone"/> is released, in ordinal name order.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="releaseMilestone"/> is negative.</exception>
        /// <exception cref="InvalidOperationException">A project has no valid milestone, so the set can't be known.</exception>
        public IReadOnlyList<string> Expected(int releaseMilestone)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(releaseMilestone);
            if (MilestoneViolations.Count != 0)
            {
                throw new InvalidOperationException(
                    "The packed set is unknown while a project has no valid milestone:\n" + string.Join("\n", MilestoneViolations));
            }

            return
            [
                .. Milestones
                    .Where(pair => pair.Value <= releaseMilestone)
                    .Select(pair => pair.Key)
                    .OrderBy(name => name, StringComparer.Ordinal),
            ];
        }
    }
}
