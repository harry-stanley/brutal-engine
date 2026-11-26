using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BrutalEngine.Architecture.Tests;

/// <summary>
/// ADR-0011: <c>BrutalEngine.Core</c>, <c>Platform</c>, <c>Graphics</c> and <c>World</c> never resolve a package that
/// brings BRUTAL's pre-release <c>Microsoft.Extensions.ObjectPool</c> dependency. Every such package (Logging,
/// Localization, the binding packages) reaches it through <c>Ahwoo.Brutal.Strings</c>, so a module's resolved
/// closure is clean exactly when its lock file contains neither <c>Ahwoo.Brutal.Strings</c> nor the pool itself.
/// BRUTAL's Core, Memory, Collections and Concurrency packages don't bring it and stay allowed.
/// </summary>
internal static class PackageClosureRule
{
    public const string Name = "Core, Platform, Graphics and World resolve no package that brings Microsoft.Extensions.ObjectPool";

    public const string LockFileName = "packages.lock.json";

    private static readonly string[] _guardedModules =
        ["BrutalEngine.Core", "BrutalEngine.Platform", "BrutalEngine.Graphics", "BrutalEngine.World"];

    private static readonly string[] _bannedPackages = ["Ahwoo.Brutal.Strings", "Microsoft.Extensions.ObjectPool"];

    /// <summary>The modules the rule applies to (ADR-0011's Decision), in layer order.</summary>
    public static IReadOnlyList<string> GuardedModules => _guardedModules;

    /// <summary>The package IDs a guarded module must not resolve. NuGet IDs are case-insensitive.</summary>
    public static IReadOnlyList<string> BannedPackages => _bannedPackages;

    /// <summary>
    /// Finds the banned packages in a <c>packages.lock.json</c>: one line per banned package that appears in any target
    /// framework or RID section, naming its resolved versions and the sections it appears in.
    /// </summary>
    /// <exception cref="InvalidDataException">The text isn't JSON in the lock-file shape.</exception>
    public static IReadOnlyList<string> FindBannedPackages(string moduleName, string lockFileJson)
    {
        ArgumentNullException.ThrowIfNull(moduleName);
        ArgumentNullException.ThrowIfNull(lockFileJson);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(lockFileJson);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"{moduleName}: {LockFileName} isn't valid JSON.", exception);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("dependencies"u8, out JsonElement sections)
                || sections.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"{moduleName}: {LockFileName} has no \"dependencies\" object.");
            }

            List<string> violations = [];
            foreach (string banned in _bannedPackages)
            {
                List<string> foundIn = [];
                SortedSet<string> versions = new(StringComparer.Ordinal);
                foreach (JsonProperty section in sections.EnumerateObject())
                {
                    if (section.Value.ValueKind != JsonValueKind.Object)
                    {
                        throw new InvalidDataException($"{moduleName}: {LockFileName} section \"{section.Name}\" isn't an object.");
                    }

                    foreach (JsonProperty package in section.Value.EnumerateObject())
                    {
                        if (string.Equals(package.Name, banned, StringComparison.OrdinalIgnoreCase))
                        {
                            foundIn.Add(section.Name);
                            versions.Add(ResolvedVersion(package.Value));
                        }
                    }
                }

                if (foundIn.Count != 0)
                {
                    violations.Add(
                        $"{moduleName} resolves {banned} {string.Join("/", versions)} in {string.Join(", ", foundIn)}. "
                        + "ADR-0011 keeps Core, Platform, Graphics and World free of Microsoft.Extensions.ObjectPool: remove the "
                        + "reference that brings it (Ahwoo.Brutal.Strings, or a package that depends on it, such as Logging, "
                        + "Localization or a binding package).");
                }
            }

            return violations;
        }
    }

    /// <summary>
    /// Applies <see cref="FindBannedPackages"/> to the lock file next to each guarded project among
    /// <paramref name="projects"/>. A guarded project without a lock file is a violation; a guarded module with no
    /// project yet is listed in <see cref="Report.NotCreated"/>, so it is checked as soon as it exists.
    /// </summary>
    /// <param name="projects">The engine projects, as <see cref="Repository.EngineSourceProjects"/> lists them.</param>
    /// <param name="readLockFile">Returns a lock file's text from its full path, or null if the file doesn't exist.</param>
    public static Report Check(IEnumerable<ProjectFile> projects, Func<string, string?> readLockFile)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(readLockFile);

        var byName = projects.ToDictionary(p => p.Name, StringComparer.Ordinal);
        List<string> checkedModules = [];
        List<string> violations = [];
        List<string> notCreated = [];
        foreach (string module in _guardedModules)
        {
            if (!byName.TryGetValue(module, out ProjectFile? project))
            {
                notCreated.Add(module);
                continue;
            }

            string lockFile = Path.Combine(Path.GetDirectoryName(project.Path)!, LockFileName);
            string? json = readLockFile(lockFile);
            if (json is null)
            {
                violations.Add(
                    $"{module} has no {LockFileName} next to {project.Path}. Engine libraries restore with a committed lock "
                    + "file, and without one this rule can't see the resolved packages.");
                continue;
            }

            checkedModules.Add(module);
            violations.AddRange(FindBannedPackages(module, json));
        }

        return new Report(checkedModules, violations, notCreated);
    }

    private static string ResolvedVersion(JsonElement package) =>
        package.ValueKind == JsonValueKind.Object
            && package.TryGetProperty("resolved"u8, out JsonElement resolved)
            && resolved.ValueKind == JsonValueKind.String
            ? resolved.GetString()!
            : "(no resolved version)";

    /// <summary>The result of <see cref="Check"/>.</summary>
    /// <param name="Checked">The guarded modules whose lock file was read.</param>
    /// <param name="Violations">One line per banned package in a guarded module, or per guarded project without a lock file.</param>
    /// <param name="NotCreated">The guarded modules that have no project under src/ yet.</param>
    internal sealed record Report(IReadOnlyList<string> Checked, IReadOnlyList<string> Violations, IReadOnlyList<string> NotCreated);
}
