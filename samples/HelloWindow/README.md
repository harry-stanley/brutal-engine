# HelloWindow

The M0 sample: an SDL3 window that shows the BRUTAL splash, then presents a Vulkan 1.3 clear-colour swapchain, with
validation counting. It is written directly on BRUTAL's SDL3 and Vulkan bindings, and will be rewritten onto
`BrutalEngine.Platform` and `BrutalEngine.Graphics` in M1.

What it does:

- Shows the official BRUTAL logo (`Assets/LOGO_BRUTAL_WHT.png`, a byte-for-byte copy of BRUTAL's) before the scene,
  as BRUTAL LICENSE 2.2 requires. See [The splash](#the-splash).
- Picks a device that has Vulkan 1.3, `VK_KHR_swapchain`, `dynamicRendering`, `synchronization2` and a queue family
  with graphics and present support. It prefers a discrete GPU and prints every rejected device with its reasons.
- Clears each swapchain image with dynamic rendering (`loadOp CLEAR`), using `synchronization2` barriers, with 2 frames
  in flight and FIFO present.
- On macOS, a published build uses the Vulkan loader, MoltenVK and its ICD manifest bundled next to the executable
  (`libvulkan.1.dylib`, `libMoltenVK.dylib`, `MoltenVK_icd.json`), so it needs no Vulkan SDK, Homebrew or
  `VK_DRIVER_FILES`. It preloads that loader by absolute path for both BRUTAL and SDL, and points `VK_DRIVER_FILES` at
  the bundled manifest unless the user already set it or `VK_ICD_FILENAMES`. An explicit `--vulkan-loader` wins.
- Ships BRUTAL's `NOTICE.txt` (LICENSE 2.3) and every BRUTAL package's third-party licences, in
  `LICENSES/<package>/`, next to the executable.
- Recreates the swapchain on OUT_OF_DATE, SUBOPTIMAL or a real change of window size. While the window is minimised it
  sleeps in `SDL_WaitEvent` instead of rendering.
- Checks every native status, and tears everything down in reverse order after `vkDeviceWaitIdle`.

## Running

```sh
dotnet run --project samples/HelloWindow -c Release -- [options]

# NativeAOT
dotnet publish samples/HelloWindow -c Release -r win-x64 -o out/hello-window
out/hello-window/HelloWindow --frames 300 --validation
```

Close the window or press Esc to quit.

| Option | Meaning |
|--------|---------|
| `--headless` | Use SDL's `offscreen` video driver (no visible window). Needs `--frames`, and a Vulkan driver with `VK_EXT_headless_surface` (see below) |
| `--frames <n>` | Exit after `n` presented frames, splash frames included. Without it, run until the window is closed |
| `--size <w>x<h>` | Initial window size (default `1280x720`) |
| `--validation` | Enable `VK_LAYER_KHRONOS_validation` with synchronization validation. Any error or warning fails the run |
| `--exercise-resize` | Shrink the window at a third of the run and restore it at two thirds. Fails unless the swapchain was recreated. Needs `--frames` |
| `--require-cpu` | Accept only a CPU device, such as lavapipe |
| `--require-driver <id>` | Accept only a device whose `VkDriverId` has this name, such as `Moltenvk` |
| `--vulkan-loader <path>` | Load the Vulkan loader from this absolute path. Fails if BRUTAL doesn't end up using it |
| `--inject-validation-error` | Record a barrier with a wrong old layout on the first frame. Implies `--validation`, so the run must exit 2 |

The validation layer comes from the Vulkan SDK (or `vulkan-validationlayers` on Linux).

## The splash

The logo is shown, unaltered except for scaling, on a black background before the first scene frame:

- **How long:** at least 2.0 s in a window, measured with a monotonic clock from the first frame, however many frames
  that takes. Headless runs have no viewer and no fixed frame rate, so they count frames instead: the first 120
  (2.0 s at 60 frames a second). Nothing can skip or shorten it. At exit the sample logs
  `splash: X frames over Y s, scene: Z frames`.
- **How it is drawn:** without shaders. `SDL_LoadPNG` decodes the logo at startup, and it is composited onto black on
  the CPU, in the swapchain's format. It is then uploaded once through a staging buffer into a device-local image.
  Each splash frame clears the swapchain image to black and `vkCmdBlitImage`s the logo into it, centred with a 32-pixel
  margin and scaled down (never up) to fit.
- **Fallback:** if the surface or format can't blit, it uses `vkCmdCopyImage` at 1:1, provided the logo fits. If
  neither works, the run fails with a setup error naming what is missing (exit 3). The splash is never silently
  skipped.
- **Limits:** the swapchain must use an 8-bit RGBA or BGRA format with `SRGB_NONLINEAR` and offer `TRANSFER_DST`
  images; every driver tried so far does. The splash clock keeps running while the window is minimised.

On the reference machine, a screenshot of the windowed splash matches the logo composited onto black pixel for pixel.

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Pass |
| 2 | The validation layer reported at least one error or warning |
| 3 | Setup failed: no suitable device, a missing layer or extension, the wrong loader, a surface that can't show the splash, or a native error during setup |
| 4 | A native call failed after setup, or `--exercise-resize` saw no swapchain recreation |
| 64 | Bad command line |

## Verified, and not verified

Verified:

- **Windows 11, NVIDIA RTX 2070 SUPER** (JIT and NativeAOT win-x64, locally): windowed runs with the splash (blit at
  1:1, and scaled down in a 400x300 window), `--exercise-resize` (including resizes during the splash),
  `--inject-validation-error`, and windowed on Mesa lavapipe for Windows. Minimise and restore (no CPU used while
  minimised) was verified before the splash existed (#24), and hasn't been rechecked since.
- **CI** (`vulkan-smoke`, JIT and NativeAOT, with validation and the negative tests), first green in
  [run 45599521930](https://github.com/harry-stanley/brutal-engine/actions/runs/45599521930):
  - **Linux** (ubuntu-24.04, Mesa llvmpipe), `--headless`.
  - **macOS** (macos-15, Apple Paravirtual device, MoltenVK), `--headless`. The NativeAOT run uses only the bundled
    loader and MoltenVK, with `VK_DRIVER_FILES` unset, and CI checks the log for both bundled paths.
  - **Windows** (windows-2025, Mesa lavapipe), windowed with `--exercise-resize`.
  - Since the splash, CI also checks that each run presented scene frames after the splash, and that the published
    output contains `NOTICE.txt` and `LICENSES/`.

Not verified yet:

- **Real Linux and macOS hardware.** Linux has only run on llvmpipe in CI; the Arch Linux machine (#27) will run it on
  the RTX 2070 SUPER. There is no Mac: macOS has only run on GitHub's paravirtual device.
- **`--headless` on Windows.** It can't run there: neither the NVIDIA driver nor Mesa lavapipe for Windows offers
  `VK_EXT_headless_surface`, so the run stops with exit 3 and says so.
- **The 1:1 copy fallback for the splash.** It is unit-tested, but every driver tried so far can blit, so no GPU run
  has used it.
- **The SUBOPTIMAL path,** which no run has produced so far.
- **Swapchain recreation without `VK_EXT_swapchain_maintenance1`.** The old swapchain's present semaphores are
  destroyed after `vkDeviceWaitIdle`. Validation accepts this, but the spec doesn't strictly guarantee that the
  presentation engine has finished with them.
