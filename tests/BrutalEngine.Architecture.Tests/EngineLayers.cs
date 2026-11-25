namespace BrutalEngine.Architecture.Tests;

/// <summary>
/// The single, declarative layer map of the engine (docs/03-architecture.md 3.1, module list in
/// docs/04-repo-layout.md, reasoning in docs/design/architecture-tests.md). It lists planned modules too, so a new
/// module only has to match its planned entry. A BrutalEngine assembly that isn't listed here fails the tests.
/// </summary>
internal static class EngineLayers
{
    public static LayerMap Map { get; } = new(
        [
            // L1 engine core.
            new("BrutalEngine.Core", 1, ModuleKind.Library),

            // L2 abstractions. Audio holds the device abstraction and our mixer (3.11): it's the abstraction L3+ sees.
            new("BrutalEngine.Platform", 2, ModuleKind.Library),
            new("BrutalEngine.Graphics", 2, ModuleKind.Library),
            new("BrutalEngine.Audio", 2, ModuleKind.Library),

            // L2 backends: the only assemblies allowed to expose Brutal.*Api binding types.
            new("BrutalEngine.Platform.Sdl3", 2, ModuleKind.Backend),
            new("BrutalEngine.Graphics.Vulkan", 2, ModuleKind.Backend),
            new("BrutalEngine.Audio.Sdl3", 2, ModuleKind.Backend),

            // L3 engine services.
            new("BrutalEngine.World", 3, ModuleKind.Library),
            new("BrutalEngine.Rendering2D", 3, ModuleKind.Library),
            new("BrutalEngine.Text", 3, ModuleKind.Library),
            new("BrutalEngine.Input", 3, ModuleKind.Library),
            new("BrutalEngine.Assets", 3, ModuleKind.Library),

            // L4 gameplay features. DebugOverlay consumes L3 services and L2 diagnostics (3.20) and is installed by
            // the L5 host, so it sits here. It drives ImGui through the SDL3 backend (docs/04), so it is a Host.
            new("BrutalEngine.Tilemaps", 4, ModuleKind.Library),
            new("BrutalEngine.Physics2D", 4, ModuleKind.Library),
            new("BrutalEngine.Animation", 4, ModuleKind.Library),
            new("BrutalEngine.UI", 4, ModuleKind.Library),
            new("BrutalEngine.Particles", 4, ModuleKind.Library),
            new("BrutalEngine.Networking", 4, ModuleKind.Library),
            new("BrutalEngine.DebugOverlay", 4, ModuleKind.Host),

            // L5 runtime host, plus the meta-package games reference (it pulls in the host and default backends).
            new("BrutalEngine.Runtime", 5, ModuleKind.Host),
            new("BrutalEngine", 5, ModuleKind.Host),

            // L6 tools (tools/). Runtime modules never reference them.
            new("BrutalEngine.Cook", 6, ModuleKind.Tool),
            new("BrutalEngine.Importers", 6, ModuleKind.Tool),
            new("BrutalEngine.Editor", 6, ModuleKind.Tool),
        ],
        [
            // Each backend implements exactly one abstraction in its own layer.
            new("BrutalEngine.Platform.Sdl3", "BrutalEngine.Platform"),
            new("BrutalEngine.Graphics.Vulkan", "BrutalEngine.Graphics"),
            new("BrutalEngine.Audio.Sdl3", "BrutalEngine.Audio"),

            // Rendering2D draws text (3.1 diagram, 3.9 -> 3.12).
            new("BrutalEngine.Rendering2D", "BrutalEngine.Text"),

            // Tilemaps generate collision shapes into Physics2D (3.14). Particles collide against it (3.18).
            new("BrutalEngine.Tilemaps", "BrutalEngine.Physics2D"),
            new("BrutalEngine.Particles", "BrutalEngine.Physics2D"),

            // The meta-package references the host (docs/04).
            new("BrutalEngine", "BrutalEngine.Runtime"),

            // The cooker is built on the shared importer library (docs/04).
            new("BrutalEngine.Cook", "BrutalEngine.Importers"),
        ]);
}
