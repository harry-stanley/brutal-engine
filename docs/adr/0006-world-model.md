# ADR-0006: World model: archetype ECS with hierarchy

- **Status:** Accepted (2026-11-25: Option A, built in-house)
- **Date:** 2026-11-25
- **Deciders:** Harry Stanley (maintainer)

## Context

The world model is the most user-visible architectural decision. It shapes how every game is written, how the editor works, serialisation, performance, threading and hot reload. Constraints from earlier decisions:

- Zero managed allocations per steady-state frame, NativeAOT (no reflection), hot-reload readiness (data must be separable from game code).
- BRUTAL's ethos and primitives: unmanaged memory, non-allocating collections, a job scheduler.
- Targets include large simulations (tens of thousands of active entities) as well as small games.
- A public engine: it must also be learnable and pleasant to use.

## Options considered

### A. Archetype ECS + first-class hierarchy (Bevy / Flecs style)

Entities are ids. Components are unmanaged structs stored in archetype tables (structure-of-arrays). Systems are functions over queries, grouped into schedules.

- **Pros:** best cache behaviour and iteration speed. Systems with declared read/write sets can run in parallel on the job system. Zero GC pressure (data in unmanaged memory). Components are plain data, so serialisation, networking replication, save games and hot reload are straightforward. Scales to large simulations. Matches BRUTAL's design.
- **Cons:** steeper learning curve for people coming from Unity or Godot. Components can't hold managed references, so they use handles for assets and strings. Structural changes during iteration need command buffers. Hierarchies and UI are awkward in a *pure* ECS, so we add relationships explicitly. Editor tooling (inspectors) needs generated metadata. It is the most engineering effort.

### B. Node / scene tree (Godot style)

Objects in a tree, each a class instance with behaviour and children.

- **Pros:** intuitive, composition by scene instancing is natural, great fit for UI and editors.
- **Cons:** an object per node means allocation and GC pressure. Virtual dispatch and pointer chasing per node give poor cache behaviour at scale. Parallelism is hard. Game logic lives in class instances, which makes hot reload, serialisation and replication harder (it needs reflection or generated code over arbitrary object graphs).

### C. GameObject + Component (classic Unity style)

Objects with lists of component class instances.

- **Pros:** most familiar to the largest audience.
- **Cons:** same performance and GC problems as B with fewer structural benefits. Unity itself moved to an ECS (DOTS) to escape them.

### Build or adopt (if A)

- **Build in-house** on `Brutal.Memory` / `Brutal.Collections`: full control over memory layout, allocation behaviour, determinism, source-generated queries and serialisation, and the hot-reload design. More work, but the world model is core engine value.
- **Adopt a C# ECS library** (e.g. Arch, Friflo.Engine.ECS): faster start. A core dependency on another project's design choices, allocation behaviour and release cadence, and it won't use BRUTAL's memory primitives.

## Decision

**Option A, built in-house:** an archetype ECS core with:

- unmanaged-struct components, generational entity ids,
- named schedules with explicit ordering and declared access, run in parallel on BRUTAL's `JobScheduler` when access sets don't conflict,
- command buffers for structural changes, applied at sync points,
- built-in `Parent`/`Children` relationships and transform propagation,
- change detection (per-component, per-chunk version counters) for efficient sync with physics, rendering and networking,
- events/messages between systems,
- scenes and prefabs as serialised entity templates (JSON in development, binary cooked),
- queries, component registration and serialisers emitted by `BrutalEngine.Generators` (AOT-safe, editor-ready metadata).

The in-game UI uses its own retained element tree (UI is a natural tree), linked to entities where needed.

To keep the learning curve manageable: an ergonomic API layer (typed `Entity` handle with `Get<T>`/`Set<T>`/`Add<T>`, query `foreach` over ref spans), clear docs, and samples that start simple.

### Performance targets to validate in spike S5

- Iterating 1,000,000 entities with 2 components: under 1 ms single-threaded on min-spec hardware.
- Spawning and despawning 100,000 entities per frame through command buffers: under 5 ms.
- Zero managed allocations for all of the above.

## Consequences

- `BrutalEngine.World` becomes the largest M1 work item. S5 must pass before the API is frozen for 0.2.
- Gameplay code is written as systems over components. Samples and docs must teach this well.
- Physics, rendering extract, animation, networking and the editor all build on change detection and generated metadata.
- If S5 fails its targets, we revisit storage (sparse sets or hybrid) inside the same API, without changing option A.
