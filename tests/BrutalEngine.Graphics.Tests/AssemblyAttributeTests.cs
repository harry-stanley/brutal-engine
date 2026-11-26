using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace BrutalEngine.Graphics.Tests;

public sealed class AssemblyAttributeTests
{
    [Fact]
    public void InternalsVisibleTo_IsExactlyTestsAndConformance()
    {
        // docs/design/graphics.md section 12: the matching test project, plus the conformance suite as the one named
        // exception to docs/07's rule. Listed with duplicates, so a second copy of either attribute fails too.
        string[] friends =
        [
            .. typeof(BufferHandle).Assembly.GetCustomAttributes<InternalsVisibleToAttribute>()
                .Select(a => a.AssemblyName)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(["BrutalEngine.Graphics.Conformance", "BrutalEngine.Graphics.Tests"], friends);
    }
}
