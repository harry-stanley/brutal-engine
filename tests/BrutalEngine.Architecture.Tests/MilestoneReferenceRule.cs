using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace BrutalEngine.Architecture.Tests;

/// <summary>
/// ADR-0016: an engine library references only engine libraries of its own or an earlier milestone. A release packs the
/// libraries whose <c>BrutalEngineMilestone</c> is at most the release milestone, and a ProjectReference between
/// libraries becomes a package dependency. A library that referenced a later milestone's library would therefore
/// ship in a package that depends on one the release doesn't pack.
/// </summary>
/// <remarks>
/// <para>
/// The rule reads the project files as text. A milestone is read exactly as <see cref="ShipGateRule"/> reads it, and a
/// library without a readable one is skipped on both sides of a reference: the ship-gate rule reports it, so the same
/// mistake isn't reported twice. Roslyn components (<see cref="LayerMap.IsRoslynComponentName"/>) are skipped on both
/// sides too: they are build-time only and never a package dependency.
/// </para>
/// <para>
/// A reference is matched to a library by the file name in its <c>Include</c>, which is the library's name. As
/// MSBuild does, the rule matches the item name <c>ProjectReference</c> in any case, splits <c>Include</c> on
/// semicolons and accepts either path separator. It is stricter than MSBuild in one way: every <c>Include</c> counts,
/// whatever its condition, and <c>Remove</c> and <c>Update</c> aren't evaluated. An <c>Include</c> whose file name
/// holds a property, item list or metadata reference or a wildcard can't be matched, so it is reported rather than
/// passed unchecked.
/// </para>
/// </remarks>
internal static class MilestoneReferenceRule
{
    public const string Name = "an engine library references only engine libraries of its own or an earlier milestone";

    /// <summary>
    /// Compares every library's milestone with the milestone of each library it references. Each violation starts
    /// with "<c>name (milestone n) </c>".
    /// </summary>
    /// <param name="libraries">The engine projects, as <see cref="Repository.EngineSourceProjects"/> lists them.</param>
    /// <param name="readProject">Returns a project file's text from its full path.</param>
    public static Report Check(IEnumerable<ProjectFile> libraries, Func<string, string> readProject)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(readProject);

        // Ordinal, as Linux resolves project paths: a reference whose name differs only in case misses below.
        Dictionary<string, int> milestones = new(StringComparer.Ordinal);
        List<(string Name, XDocument Document)> readable = [];
        foreach (ProjectFile library in libraries)
        {
            if (LayerMap.IsRoslynComponentName(library.Name))
            {
                continue;
            }

            XDocument document;
            try
            {
                document = XDocument.Parse(readProject(library.Path));
            }
            catch (XmlException)
            {
                // The ship-gate rule reports a project file that isn't XML.
                continue;
            }

            if (ShipGateRule.ReadMilestone(document, ShipGateRule.MilestoneProperty, out int milestone) is null)
            {
                milestones.Add(library.Name, milestone);
                readable.Add((library.Name, document));
            }
        }

        List<string> checkedReferences = [];
        List<string> violations = [];
        foreach ((string name, XDocument document) in readable)
        {
            int milestone = milestones[name];
            foreach (string include in ReferenceIncludes(document))
            {
                string referenced = Path.GetFileNameWithoutExtension(include.Replace('\\', '/'));
                if (!IsLiteral(referenced))
                {
                    violations.Add(
                        $"{name} (milestone {milestone}) has the ProjectReference \"{include}\", whose project name isn't written "
                        + "out, so the milestone of what it references can't be checked. Write the project file's name in the "
                        + "Include; see ADR-0016.");
                    continue;
                }

                if (!milestones.TryGetValue(referenced, out int referencedMilestone))
                {
                    // Not a library with a readable milestone, so there is nothing to compare. The cases that land here,
                    // and where each one is reported instead:
                    // - a name that differs from a library's only in letter case: the Linux build, a required check,
                    //   fails with MSB9008 (the referenced project doesn't exist);
                    // - a library without a readable milestone: the ship-gate rule;
                    // - a project that isn't an engine library: the layering rule, which rejects a runtime library's
                    //   reference to a tool or to an unknown BrutalEngine module;
                    // - a Roslyn component: nowhere, as it is build-time only and never a package dependency.
                    continue;
                }

                checkedReferences.Add($"{name} -> {referenced}");
                if (referencedMilestone > milestone)
                {
                    violations.Add(
                        $"{name} (milestone {milestone}) references {referenced} (milestone {referencedMilestone}): a library may "
                        + "reference only libraries of its own or an earlier milestone. The release of milestone "
                        + $"{milestone} packs {name} but not {referenced}, so the {name} package would depend on a package that "
                        + "isn't packed. Remove the reference, or change one library's "
                        + $"{ShipGateRule.MilestoneProperty} so that {referenced} ships no later than {name}; see ADR-0016.");
                }
            }
        }

        return new Report(checkedReferences, violations);
    }

    /// <summary>Every distinct path in the Include of a ProjectReference item, in document order.</summary>
    private static IEnumerable<string> ReferenceIncludes(XDocument document) =>
        document.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "ProjectReference", StringComparison.OrdinalIgnoreCase))
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .SelectMany(include => include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// False when MSBuild would expand the name: it holds a property (<c>$</c>), item list (<c>@</c>) or metadata
    /// (<c>%</c>) reference, or a wildcard (<c>*</c>, <c>?</c>).
    /// </summary>
    private static bool IsLiteral(string projectName) => projectName.IndexOfAny(['$', '@', '%', '*', '?']) < 0;

    /// <summary>The result of <see cref="Check"/>.</summary>
    /// <param name="Checked">
    /// One "<c>from -> to</c>" line per reference whose two milestones were compared, in the order the libraries and
    /// their references were given.
    /// </param>
    /// <param name="Violations">
    /// One line per reference to a later milestone's library, or per reference whose project name can't be read.
    /// </param>
    internal sealed record Report(IReadOnlyList<string> Checked, IReadOnlyList<string> Violations);
}
