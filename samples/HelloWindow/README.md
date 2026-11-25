# HelloWindow

The M0 sample: an SDL3 window presenting a Vulkan 1.3 clear-colour swapchain, with validation counting. It is written
directly on BRUTAL's SDL3 and Vulkan bindings, and will be rewritten onto `BrutalEngine.Platform` and
`BrutalEngine.Graphics` in M1.

What it does:

- Picks a device that has Vulkan 1.3, `VK_KHR_swapchain`, `dynamicRendering`, `synchronization2` and a queue family
  with graphics and present support. It prefers a discrete GPU and prints every rejected device with its reasons.
- Clears each swapchain image with dynamic rendering (`loadOp CLEAR`), using `synchronization2` barriers, with 2 frames
  in flight and FIFO present.
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
| `--frames <n>` | Exit after `n` presented frames. Without it, run until the window is closed |
| `--size <w>x<h>` | Initial window size (default `1280x720`) |
| `--validation` | Enable `VK_LAYER_KHRONOS_validation` with synchronization validation. Any error or warning fails the run |
| `--exercise-resize` | Shrink the window at a third of the run and restore it at two thirds. Fails unless the swapchain was recreated. Needs `--frames` |
| `--require-cpu` | Accept only a CPU device, such as lavapipe |
| `--require-driver <id>` | Accept only a device whose `VkDriverId` has this name, such as `Moltenvk` |
| `--vulkan-loader <path>` | Load the Vulkan loader from this absolute path. Fails if BRUTAL doesn't end up using it |
| `--inject-validation-error` | Record a barrier with a wrong old layout on the first frame. Implies `--validation`, so the run must exit 2 |

The validation layer comes from the Vulkan SDK (or `vulkan-validationlayers` on Linux).

## Exit codes

| Code | Meaning |
|------|---------|
| 0 | Pass |
| 2 | The validation layer reported at least one error or warning |
| 3 | Setup failed: no suitable device, a missing layer or extension, the wrong loader, or a native error during setup |
| 4 | A native call failed after setup, or `--exercise-resize` saw no swapchain recreation |
| 64 | Bad command line |

## Verified, and not verified

Verified on Windows 11 with an NVIDIA RTX 2070 SUPER (JIT and NativeAOT win-x64): windowed runs, `--exercise-resize`,
`--inject-validation-error`, and minimise and restore (no CPU used while minimised).

Not verified yet:

- **Linux and macOS.** The linux-x64 and osx-arm64 builds compile but haven't been run. CI (#25) and the Arch Linux
  machine (#27) will run them.
- **`--headless`.** It can't run on Windows: neither the NVIDIA driver nor Mesa lavapipe for Windows offers
  `VK_EXT_headless_surface`, so the run stops with exit 3 and says so. It is expected to work with Mesa drivers on
  Linux, which hasn't been tried yet.
- **The SUBOPTIMAL path,** which no run has produced so far.
- **Swapchain recreation without `VK_EXT_swapchain_maintenance1`.** The old swapchain's present semaphores are
  destroyed after `vkDeviceWaitIdle`. Validation accepts this, but the spec doesn't strictly guarantee that the
  presentation engine has finished with them.
