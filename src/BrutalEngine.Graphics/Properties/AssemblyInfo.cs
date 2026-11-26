using System.Runtime.CompilerServices;

// The matching test project (docs/07-coding-standards.md). Declared here, not inherited from src/Directory.Build.props
// (the csproj removes that item), so this file lists every assembly that sees Graphics internals.
[assembly: InternalsVisibleTo("BrutalEngine.Graphics.Tests")]

// The one named exception to docs/07's "matching test project only" rule: the conformance suite forces device loss on
// every backend through the front end's internal fault seam (docs/design/graphics.md section 12).
[assembly: InternalsVisibleTo("BrutalEngine.Graphics.Conformance")]
