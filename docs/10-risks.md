# 10. Risks

Each risk has an owner action. Spikes retire risks with evidence. Their results are written back here (status column) or as ADRs.

| ID | Risk | Impact | Likelihood | Mitigation | Status |
|----|------|--------|------------|------------|--------|
| R1 | **macOS is not officially supported by BRUTAL.** MoltenVK and the Vulkan loader aren't bundled (`Brutal.Vulkan/ImportResolver.cs` has a TODO for it), and nobody upstream guarantees macOS behaviour | High: one of three platforms | High | We build, bundle, sign and test MoltenVK and the loader ourselves (`natives.yml`). A dedicated macOS CI job plus self-hosted Mac nightly. Spike S1. Report macOS issues upstream with repros | Open (S1) |
| R2 | **No GPU on hosted CI runners**, so GPU code could be "tested" only on software drivers that differ from real hardware | High | Certain | Software Vulkan (lavapipe/SwiftShader) for every PR, self-hosted real-GPU nightly runs, conformance suite with validation layers. Spike S3 | Open (S3) |
| R3 | **Metal unavailable on GitHub macOS runners**, so no MoltenVK GPU tests on PRs | Medium | Medium | macOS GPU tests move to the self-hosted Mac (nightly + pre-release). PRs still build and run all non-GPU tests on macOS | Open (S3) |
| R4 | **BRUTAL packages not fully NativeAOT/trim clean**, or other runtime surprises with published packages | High: release format | Low to medium | Spike S2. Report upstream. Pin known-good versions. As a last resort, JIT self-contained publish as a documented fallback for affected platforms | Open (S2) |
| R5 | **Scope:** v1 includes every feature group (up to networking). This is a full commercial engine | High: schedule | High | Strict milestone order with a release per milestone. Later milestones can be cut from 1.0 into 1.x by an ADR without invalidating earlier work. No parallel half-finished subsystems | Accepted, managed |
| R6 | **BRUTAL API churn** (CalVer, pre-1.0 style evolution) breaks us | Medium | Medium | Central version pinning. BRUTAL bumps as dedicated PRs with full CI. Backend projects isolate binding usage, so churn stays local | Managed |
| R7 | **Complex-script text shaping** (HarfBuzz) not available through BRUTAL in a usable form | Medium: localisation quality | Medium | Spike S8 at the start of M3 (`Ahwoo.Brutal.Sdl.Ttf` HarfBuzz, or bundling HarfBuzz ourselves). Until resolved, document LTR-only support | Open (S8) |
| R8 | **RakNet is unmaintained upstream**, a security and maintenance risk for a public engine | Medium | High (if chosen) | Spike S7 compares it with an in-house UDP transport. Either choice gets fuzzing and a security review | Open (S7) |
| R9 | **Name and trademark:** "BrutalEngine" uses Ahwoo's BRUTAL name. The licence grants copyright, not trademark, permission | Medium: rename cost grows over time | Unknown | Ask Ahwoo before the first nuget.org publish. Until then, packages are only CI artifacts. A rename at M0 costs little | Open (Q5) |
| R10 | **BRUTAL licence duties for downstream games** (logo splash, credits, NOTICE) are easy for game developers to miss | Medium: legal exposure for users | Medium | Compliance on by default (splash, credits API, publish copies notices, test verifies). Documented prominently in the manual | Mitigated by design |
| R11 | **Single maintainer / reviewer bottleneck** | Medium | High | Small PRs, strong automation (CI gates do the mechanical review), design docs before big PRs so review is incremental | Managed |
| R12 | **Self-hosted runners on a public repo** could run malicious PR code | High: security | Low with controls | Never run self-hosted jobs on `pull_request`, ephemeral runners, unprivileged user (see [CI/CD](06-ci-cd-and-release.md#self-hosted-runners)) | Mitigated by design |
| R13 | **Audio decoding dependency** for compressed streaming (Vorbis/Opus) not provided by BRUTAL in a mixer-friendly form | Low to medium | Medium | Spike S6 at the start of M2: a managed decoder (licence-checked, AOT-checked) vs SDL3_mixer's decoders (`Ahwoo.Brutal.Sdl.Mixer`) vs bundling a C decoder | Open (S6) |
| R14 | **Box2D cross-platform determinism** may not hold for our usage | Low to medium: affects networking design | Low to medium | Cross-OS determinism test from M3. If it fails, networking uses server authority without lockstep (already the plan) | Open |

## Spike schedule

| Spike | Topic | When |
|-------|-------|------|
| S1 | SDL3 + Vulkan + bundled MoltenVK on all OSes | M0 |
| S2 | NativeAOT with BRUTAL | M0 |
| S3 | Software Vulkan / Metal on CI runners | M0 |
| S4 | Slang offline compilation | M0 |
| S5 | World model prototype benchmark | M0 (after ADR-0006) |
| S6 | Compressed audio decoding | Start of M2 |
| S7 | Networking transport | Start of M5 |
| S8 | Complex-script text shaping | Start of M3 |
