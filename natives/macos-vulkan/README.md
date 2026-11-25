# macOS Vulkan natives

BRUTAL ships no Vulkan loader or driver for macOS, so BrutalEngine bundles its own (see
[tech stack](../../docs/02-tech-stack.md) and [risk R1](../../docs/10-risks.md)). This folder holds the pinned
versions and the scripts that produce them, as proven by spike S1/S3 ([results](../../docs/spikes/README.md)).

| File | What it does |
|---|---|
| `pins.env` | Pinned versions: MoltenVK by release tag + asset SHA-256, the loader and the validation layers by tag + commit |
| `fetch-moltenvk.sh` | Downloads the MoltenVK release asset, verifies its SHA-256, writes `libMoltenVK.dylib` and `MoltenVK_icd.json` |
| `build-from-source.sh` | Builds `libvulkan.1.dylib` and the Khronos validation layer (arm64) from the pinned tags, asserting each commit |

The layer build takes 6-13 minutes on a GitHub macOS runner, so CI caches it. The validation layers are a test and
development tool only: games ship the loader, MoltenVK and the ICD manifest.

Today the scripts are used by the `vulkan-smoke` CI job through
[`.github/actions/setup-macos-vulkan`](../../.github/actions/setup-macos-vulkan/action.yml). The planned `natives.yml`
workflow will build the `BrutalEngine.Natives.MoltenVK` package from the same pins
([CI/CD](../../docs/06-ci-cd-and-release.md)).

At run time, the bundled loader needs two things from its host (the smoke harness today, the Vulkan backend later):

1. The loader is preloaded by absolute path, because BRUTAL's resolver doesn't search the app directory.
2. `VK_DRIVER_FILES` points at the bundled `MoltenVK_icd.json`, because the loader doesn't find MoltenVK by itself.

To bump a version, change its tag together with its commit or SHA-256. The scripts refuse mismatches, and the CI build
cache is keyed on the pins and on `build-from-source.sh`.
