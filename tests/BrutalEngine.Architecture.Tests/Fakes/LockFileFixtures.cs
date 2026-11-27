namespace BrutalEngine.Architecture.Tests.Fakes;

/// <summary>
/// Trimmed <c>packages.lock.json</c> files in the shape NuGet writes them (version 2: one section per target framework
/// and one per target framework and RID), for the package-closure rule's unit tests.
/// </summary>
internal static class LockFileFixtures
{
    /// <summary><c>Ahwoo.BRUTAL.Strings</c> in the target framework section, brought by a binding package.</summary>
    public const string WithStrings =
        """
        {
          "version": 2,
          "dependencies": {
            "net11.0": {
              "Ahwoo.Brutal.Vulkan": {
                "type": "Direct",
                "requested": "[2026.10.3, )",
                "resolved": "2026.10.3",
                "contentHash": "fake",
                "dependencies": {
                  "Ahwoo.BRUTAL.Strings": "2026.10.2"
                }
              },
              "Ahwoo.BRUTAL.Strings": {
                "type": "Transitive",
                "resolved": "2026.10.2",
                "contentHash": "fake"
              }
            },
            "net11.0/linux-x64": {},
            "net11.0/win-x64": {}
          }
        }
        """;

    /// <summary><c>Microsoft.Extensions.ObjectPool</c> only in a RID section (with different casing, as IDs are case-insensitive).</summary>
    public const string WithObjectPoolInRidSection =
        """
        {
          "version": 2,
          "dependencies": {
            "net11.0": {
              "Ahwoo.Brutal.Memory": {
                "type": "Direct",
                "requested": "[2026.10.2, )",
                "resolved": "2026.10.2",
                "contentHash": "fake"
              }
            },
            "net11.0/linux-x64": {},
            "net11.0/win-x64": {
              "microsoft.extensions.objectpool": {
                "type": "Transitive",
                "resolved": "11.0.0",
                "contentHash": "fake"
              }
            }
          }
        }
        """;

    /// <summary>Only allowed BRUTAL packages (<c>Ahwoo.Brutal.Memory</c> and its <c>Ahwoo.Brutal.Core</c>).</summary>
    public const string WithOnlyBrutalMemory =
        """
        {
          "version": 2,
          "dependencies": {
            "net11.0": {
              "Ahwoo.Brutal.Memory": {
                "type": "Direct",
                "requested": "[2026.10.2, )",
                "resolved": "2026.10.2",
                "contentHash": "fake",
                "dependencies": {
                  "Ahwoo.Brutal.Core": "2026.10.4"
                }
              },
              "Ahwoo.Brutal.Core": {
                "type": "Transitive",
                "resolved": "2026.10.4",
                "contentHash": "fake"
              }
            },
            "net11.0/linux-x64": {},
            "net11.0/osx-arm64": {},
            "net11.0/win-x64": {}
          }
        }
        """;

    /// <summary>Both banned packages, each in the target framework section and a RID section.</summary>
    public const string WithBothInSeveralSections =
        """
        {
          "version": 2,
          "dependencies": {
            "net11.0": {
              "Ahwoo.BRUTAL.Strings": { "type": "Transitive", "resolved": "2026.10.2", "contentHash": "fake" },
              "Microsoft.Extensions.ObjectPool": { "type": "CentralTransitive", "resolved": "11.0.0", "contentHash": "fake" }
            },
            "net11.0/linux-x64": {
              "Ahwoo.BRUTAL.Strings": { "type": "Transitive", "resolved": "2026.10.2", "contentHash": "fake" },
              "Microsoft.Extensions.ObjectPool": { "type": "CentralTransitive", "resolved": "11.0.0", "contentHash": "fake" }
            }
          }
        }
        """;

    /// <summary>Valid JSON without the lock file's <c>dependencies</c> object.</summary>
    public const string WithoutDependencies =
        """
        {
          "version": 2
        }
        """;
}
