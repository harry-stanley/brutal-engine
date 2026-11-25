# BrutalEngine

An open-source, production-grade 2D game engine for C#, running on Windows, Linux and macOS. Built on the [BRUTAL framework](https://github.com/ahwootech/BRUTAL) and .NET 10.

> **Status: planning.** No code yet. The engineering plan lives in [docs/](docs/README.md).

## At a glance

- Code-first engine shipped as NuGet packages. Games are ordinary .NET 10 applications.
- Vulkan 1.3 behind an abstract render interface (MoltenVK on macOS), SDL3 for platform, input and audio.
- Data-oriented world model: an in-house archetype ECS with hierarchy ([ADR-0006](docs/adr/0006-world-model.md)).
- Offline asset cooking into versioned packs, with hot reload during development.
- NativeAOT release builds for win-x64, linux-x64 and osx-arm64.
- Planned v1 scope: sprites, cameras, input, audio, text, tilemaps, Box2D physics, sprite and skeletal animation, in-game UI, localisation, 2D lighting and post-processing, particles, networking. See the [roadmap](docs/09-roadmap.md).

## Licence

BrutalEngine is MIT licensed (see [LICENSE](LICENSE)).

It is built with BRUTAL, which is used under the BRUTAL License v1.0. Games made with BrutalEngine must meet BRUTAL's obligations: the BRUTAL copyright notice in the credits, the BRUTAL logo shown before gameplay, and [NOTICE.txt](NOTICE.txt) distributed with the game. The engine does all three by default. See [Vision and scope, licensing obligations](docs/01-vision-and-scope.md#licensing-obligations).
