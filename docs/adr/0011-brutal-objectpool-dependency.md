# ADR-0011: Accept BRUTAL's .NET 11 `Microsoft.Extensions.ObjectPool` dependency, pinned to GA

- **Status:** Accepted; amended by [ADR-0015](0015-tested-platforms-windows-linux.md) (the pin is proven in CI on win-x64 and linux-x64 only)
- **Date:** 2026-11-26
- **Deciders:** the maintainer

## Context

Risk R16 ([Risks](../10-risks.md), docs/10-risks.md:22) found that `Ahwoo.Brutal.Strings` depends on a .NET 11 pre-release package. Re-checked from the nuspecs in `~/.nuget/packages/<id>/<version>/<id>.nuspec` on 2026-11-26:

- `Ahwoo.Brutal.Strings` 2026.10.2 depends on `Microsoft.Extensions.ObjectPool` **11.0.0-preview.5.26302.115**. 2026.10.2 is the latest Strings release on nuget.org (flat container index, 2026-11-26).
- It reaches every BRUTAL binding package the engine plans to use:
  - `Ahwoo.Brutal.Sdl` 2026.10.3 -> `Ahwoo.Brutal.Logging` 2026.10.3 -> `Strings` 2026.10.2, so `Platform.Sdl3` (and later `Audio.Sdl3`) gets it, not only `Graphics.Vulkan`;
  - `Ahwoo.Brutal.Vulkan` 2026.10.3 and `Ahwoo.Brutal.Vulkan.Abstractions` 2026.10.3 -> `Strings` directly, and through `Logging`;
  - `Ahwoo.Brutal.ImGui` 2026.10.3 -> `Strings` directly (DebugOverlay, [ADR-0013](0013-imgui-without-brutal-backends.md));
  - `Ahwoo.Brutal.Slang` 2026.10.3 -> `Strings` directly (the cook tool, [ADR-0012](0012-m1-shader-cooking.md)).
- The M0 `HelloWindow` sample and the AOT smoke harness already ship it: their lock files resolve `Microsoft.Extensions.ObjectPool` to `11.0.0-preview.5.26302.115` (`samples/HelloWindow/packages.lock.json:230-233`, `tests/BrutalEngine.Aot.Smoke/packages.lock.json:205-208`).
- The only BRUTAL code that uses it is `Brutal.Strings/StringBuilder/StringBuilderPool.cs:12`, a `DefaultObjectPool<StringBuilder<TChar>>`. `Brutal.Logging` rents from it whenever a log line is built (`Brutal.Logging/LogString.cs:76`).
- **It can't be pinned down.** NuGet won't resolve a version below a dependency's minimum. The upstream issue draft (#2) reproduced this on .NET 10.0.15: pinning to the .NET 10 GA line (`10.0.x`) fails restore with NU1109 under `CentralPackageTransitivePinningEnabled` (which we use, `Directory.Packages.props:5`), and with NU1605 as a direct reference. `Brutal.Strings.dll` is compiled against ObjectPool assembly version 11.0.0.0, so forcing a 10.0.x assembly fails the build or the first pooled string at run time.
- **It can be pinned up.** `Microsoft.Extensions.ObjectPool` **11.0.0** (GA, MIT licence) was published on nuget.org on 2026-11-10. It satisfies Strings' `>= 11.0.0-preview.5.26302.115` range. Like the preview, it has `net11.0`, `net462` and `netstandard2.0` libs, so a .NET 10 app gets the `netstandard2.0` build either way.
- **Probe (Windows only).** A throwaway project on 2026-11-26 (Windows 11, .NET SDK 10.0.404, JIT, `net10.0`) referenced `Ahwoo.Brutal.Logging` 2026.10.3 with transitive pinning on and `Microsoft.Extensions.ObjectPool` pinned to 11.0.0. Restore resolved `Microsoft.Extensions.ObjectPool/11.0.0`. At run time it rented and returned a builder from `Brutal.Strings.StringBuilderPool<char>.Shared`, and the loaded ObjectPool assembly reported informational version `11.0.0+297acf930fb0f5c07fd4e51d78e521b4ad2e5d1f`. An independent reproduction of the same probe also published it with `dotnet publish -r win-x64 -p:PublishAot=true`: 0 warnings, and the native executable ran the same pool calls and exited 0, with `Brutal.Strings` bound to ObjectPool assembly version 11.0.0.0. Pinning the same probe to 10.0.15 instead fails restore with NU1109 (package downgrade from `11.0.0-preview.5.26302.115`). **Not run:** NativeAOT on linux-x64 or osx-arm64, JIT on Linux or macOS, or the engine's real BRUTAL code paths.
- Only BRUTAL's publisher can change the published nuspec (upstream issue draft #2, not filed yet).
- Note: the architecture tests check layering and public binding surfaces, not package references ([architecture tests](../design/architecture-tests.md), docs/design/architecture-tests.md:63-73). Nothing automated stops a module such as Core from referencing a BRUTAL package today. Core is BRUTAL-free by design ([memory design](../design/memory.md), docs/design/memory.md:17).
- Not every BRUTAL package brings it. `Ahwoo.Brutal.Core` 2026.10.4 depends only on `Microsoft.Extensions.Logging` (+ `.Console`) 10.0.0 and `CommunityToolkit.HighPerformance` 8.4.2; `Ahwoo.Brutal.Memory` 2026.10.2 on `Core` and `Microsoft.Extensions.Logging` (+ `.Console`); `Ahwoo.Brutal.Collections` 2026.10.2 on `Memory`, `Microsoft.Extensions.Logging.Console` and `CommunityToolkit.HighPerformance`; and `Ahwoo.Brutal.Concurrency` 2026.10.2 on nothing (their nuspecs, 2026-11-26; these are the latest releases on nuget.org). [ADR-0006](0006-world-model.md) builds World on `Brutal.Memory` and `Brutal.Collections` and runs schedules on BRUTAL's `JobScheduler` (docs/adr/0006-world-model.md:41, :49; [Architecture 3.6](../03-architecture.md#36-world-model)).

## Options considered

1. **Accept the transitive package in the assemblies that reference BRUTAL binding packages, pinned centrally to the 11.0.0 GA release:** never referenced directly by engine code, tracked upstream (upstream issue draft #2), and the pin removed once BRUTAL ships a GA dependency; Core, Platform, Graphics and World stay free of it. Pros: unblocks M1; nothing we ship carries a pre-release package; the GA package gets servicing. Cons: a .NET 11 package version in a .NET 10 LTS product, and a combination BRUTAL wasn't built against (same assembly version 11.0.0.0).
2. **Accept it as BRUTAL declares it, pinned at 11.0.0-preview.5.26302.115:** what HelloWindow ships today. Cons: a stale pre-release with no servicing ships in every game.
3. **Block the backends until upstream ships a GA dependency.** Cons: stops M1, with no date.
4. **Fork or vendor the needed BRUTAL code.** Cons: against "edit nothing in BRUTAL" ([coding standards](../07-coding-standards.md)) in spirit, and doubles maintenance.

## Decision

Decision (maintainer, 2026-11-26): option 1. Assemblies that reference BRUTAL binding packages (the L2 backends, DebugOverlay, and the L6 tools and samples, which may use any layer) accept `Microsoft.Extensions.ObjectPool` as a transitive dependency of `Ahwoo.Brutal.Strings`, pinned centrally to 11.0.0 GA. Engine code never references it directly. `BrutalEngine.Core`, `BrutalEngine.Platform`, `BrutalEngine.Graphics` and `BrutalEngine.World` never reference a package that brings it: `Ahwoo.Brutal.Strings`, or anything that depends on it (`Logging`, `Localization` and the binding packages). BRUTAL packages without it, such as `Core`, `Memory`, `Collections` and `Concurrency`, stay allowed where the layer rules and other ADRs allow them. The pin is removed when BRUTAL itself depends on a GA `Microsoft.Extensions.ObjectPool`.

## Consequences

- **The pin lands with its first user.** This ADR adds no package version. The first issue that adds a BRUTAL reference to an engine project adds `<PackageVersion Include="Microsoft.Extensions.ObjectPool" Version="11.0.0" />` to `Directory.Packages.props` with a comment linking this ADR, refreshes every lock file that resolves the package (HelloWindow and the AOT smoke harness included), and proves the pin under NativeAOT on win-x64, linux-x64 and osx-arm64 in CI.
- **Never referenced directly:** no engine code uses `Microsoft.Extensions.ObjectPool` types.
- **Core, the abstractions and World stay free of it:** Core, Platform, Graphics and World reference no package that brings it: `Ahwoo.Brutal.Strings`, or anything that depends on it (`Logging`, `Localization` and the binding packages). `Ahwoo.Brutal.Memory`, `Collections`, `Concurrency` and `Core` (BRUTAL's) don't bring it in their 2026.10.x releases above, so they stay allowed: World uses them under ADR-0006. Our Core also stays BRUTAL-free entirely, by its own design. Until an architecture test checks package references, this is enforced in review, against the nuspecs of the pinned versions.
- **Tracked upstream:** upstream issue draft #2 asks BRUTAL to depend on the .NET 10 GA line. R16 links this ADR.
- **Exit condition:** when a Strings release depends on a GA `Microsoft.Extensions.ObjectPool`, the BRUTAL-bump PR removes the pin and supersedes this ADR.
- **Revisit if:**
  - the pinned 11.0.0 assembly fails a BRUTAL code path under NativeAOT on linux-x64 or osx-arm64, or on Linux or macOS at all (only the Windows probe, JIT and NativeAOT, has run);
  - a BRUTAL package used by Platform, Graphics or World (such as `Memory`, `Collections` or `Concurrency`) gains a dependency on `Strings` or ObjectPool;
  - more BRUTAL packages gain pre-release dependencies;
  - BRUTAL starts using ObjectPool APIs that differ between the preview and GA builds.
