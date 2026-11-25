# ADR-0011: Accept BRUTAL's .NET 11 `Microsoft.Extensions.ObjectPool` dependency, pinned to GA

- **Status:** Accepted
- **Date:** 2026-11-26
- **Deciders:** the maintainer

## Context

Risk R16 ([Risks](../10-risks.md), docs/10-risks.md:22) found that `Ahwoo.Brutal.Strings` depends on a .NET 11 pre-release package. Re-checked from the nuspecs in `~/.nuget/packages/<id>/<version>/<id>.nuspec` on 2026-11-26:

- `Ahwoo.Brutal.Strings` 2026.10.2 depends on `Microsoft.Extensions.ObjectPool` **11.0.0-preview.5.26302.115**. 2026.10.2 is the latest Strings release on nuget.org (flat container index, 2026-11-26).
- It reaches every BRUTAL binding package the engine plans to use:
  - `Ahwoo.Brutal.Sdl` 2026.10.3 -> `Ahwoo.Brutal.Logging` 2026.10.3 -> `Strings` 2026.10.2, so `Platform.Sdl3` (and later `Audio.Sdl3`) gets it, not only `Graphics.Vulkan`;
  - `Ahwoo.Brutal.Vulkan` 2026.10.3 and `Ahwoo.Brutal.Vulkan.Abstractions` 2026.10.3 -> `Strings` directly, and through `Logging`;
  - `Ahwoo.Brutal.ImGui` 2026.10.3 -> `Strings` directly (DebugOverlay, ADR-0013);
  - `Ahwoo.Brutal.Slang` 2026.10.3 -> `Strings` directly (the cook tool, [ADR-0012](0012-m1-shader-cooking.md)).
- The M0 `HelloWindow` sample and the AOT smoke harness already ship it: their lock files resolve `Microsoft.Extensions.ObjectPool` to `11.0.0-preview.5.26302.115` (`samples/HelloWindow/packages.lock.json:230-233`, `tests/BrutalEngine.Aot.Smoke/packages.lock.json:205-208`).
- The only BRUTAL code that uses it is `Brutal.Strings/StringBuilder/StringBuilderPool.cs:12`, a `DefaultObjectPool<StringBuilder<TChar>>`. `Brutal.Logging` rents from it whenever a log line is built (`Brutal.Logging/LogString.cs:76`).
- **It can't be pinned down.** NuGet won't resolve a version below a dependency's minimum. The upstream issue draft (#2) reproduced this on .NET 10.0.15: pinning to the .NET 10 GA line (`10.0.x`) fails restore with NU1109 under `CentralPackageTransitivePinningEnabled` (which we use, `Directory.Packages.props:5`), and with NU1605 as a direct reference. `Brutal.Strings.dll` is compiled against ObjectPool assembly version 11.0.0.0, so forcing a 10.0.x assembly fails the build or the first pooled string at run time.
- **It can be pinned up.** `Microsoft.Extensions.ObjectPool` **11.0.0** (GA, MIT licence) was published on nuget.org on 2026-11-10. It satisfies Strings' `>= 11.0.0-preview.5.26302.115` range. Like the preview, it has `net11.0`, `net462` and `netstandard2.0` libs, so a .NET 10 app gets the `netstandard2.0` build either way.
- **Probe (Windows only).** A throwaway project on 2026-11-26 (Windows 11, .NET SDK 10.0.404, JIT, `net10.0`) referenced `Ahwoo.Brutal.Logging` 2026.10.3 with transitive pinning on and `Microsoft.Extensions.ObjectPool` pinned to 11.0.0. Restore resolved `Microsoft.Extensions.ObjectPool/11.0.0`. At run time it rented and returned a builder from `Brutal.Strings.StringBuilderPool<char>.Shared`, and the loaded ObjectPool assembly reported informational version `11.0.0+297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f`. **Not run:** NativeAOT, Linux, macOS, or the engine's real BRUTAL code paths.
- Only BRUTAL's publisher can change the published nuspec (upstream issue draft #2, not filed yet).
- Correction to the planning notes: the architecture tests check layering and public binding surfaces, not package references ([architecture tests](../design/architecture-tests.md), docs/design/architecture-tests.md:63-73). Nothing automated stops a module such as Core from referencing a BRUTAL package today. Core is BRUTAL-free by design ([memory design](../design/memory.md)).

## Options considered

1. **Accept the transitive package in the assemblies that reference BRUTAL binding packages, pinned centrally to the 11.0.0 GA release:** never referenced directly by engine code, tracked upstream (upstream issue draft #2), and the pin removed once BRUTAL ships a GA dependency; Core, Platform, Graphics and World stay free of it. Pros: unblocks M1; nothing we ship carries a pre-release package; the GA package gets servicing. Cons: a .NET 11 package version in a .NET 10 LTS product, and a combination BRUTAL wasn't built against (same assembly version 11.0.0.0).
2. **Accept it as BRUTAL declares it, pinned at 11.0.0-preview.5.26302.115:** what HelloWindow ships today. Cons: a stale pre-release with no servicing ships in every game.
3. **Block the backends until upstream ships a GA dependency.** Cons: stops M1, with no date.
4. **Fork or vendor the needed BRUTAL code.** Cons: against "edit nothing in BRUTAL" ([coding standards](../07-coding-standards.md)) in spirit, and doubles maintenance.

## Decision

Decision (maintainer, 2026-11-26): option 1. Assemblies that reference BRUTAL binding packages (the L2 backends, DebugOverlay, and the L6 tools and samples, which may use any layer) accept `Microsoft.Extensions.ObjectPool` as a transitive dependency of `Ahwoo.Brutal.Strings`, pinned centrally to 11.0.0 GA. Engine code never references it directly. `BrutalEngine.Core`, `BrutalEngine.Platform`, `BrutalEngine.Graphics` and `BrutalEngine.World` never reference a package that brings it. The pin is removed when BRUTAL itself depends on a GA `Microsoft.Extensions.ObjectPool`.

## Consequences

- **The pin lands with its first user.** This ADR adds no package version. The first issue that adds a BRUTAL reference to an engine project adds `<PackageVersion Include="Microsoft.Extensions.ObjectPool" Version="11.0.0" />` to `Directory.Packages.props` with a comment linking this ADR, refreshes every lock file that resolves the package (HelloWindow and the AOT smoke harness included), and proves the pin under NativeAOT on win-x64, linux-x64 and osx-arm64 in CI.
- **Never referenced directly:** no engine code uses `Microsoft.Extensions.ObjectPool` types.
- **Abstractions stay free of it:** Core, Platform, Graphics and World reference no BRUTAL package. Until an architecture test checks package references, this is enforced in review.
- **Tracked upstream:** upstream issue draft #2 asks BRUTAL to depend on the .NET 10 GA line. R16 links this ADR.
- **Exit condition:** when a Strings release depends on a GA `Microsoft.Extensions.ObjectPool`, the BRUTAL-bump PR removes the pin and supersedes this ADR.
- **Revisit if:**
  - the pinned 11.0.0 assembly fails a BRUTAL code path under NativeAOT, or on Linux or macOS (only Windows JIT has been probed);
  - more BRUTAL packages gain pre-release dependencies;
  - BRUTAL starts using ObjectPool APIs that differ between the preview and GA builds.
