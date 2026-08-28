# Client MeshRef Leak Fix (`vsvaogc`)

Client-only Vintage Story mod. When the game garbage-collects an undisposed `MeshRef` / OpenGL VAO, this actually frees the GPU object on the render thread instead of leaking VRAM.

Stops the session-long FPS slide and hitching from MeshRef leaks (skeps, handbook, HD meshes, GUI leftovers).

**ModDB:** https://mods.vintagestory.at/vsvaogc

## Install

Use the DLL-only zip from ModDB. Do **not** drop this source folder into `Mods`. Vintage Story will try to compile `src/` / `.cs` and skip the ModSystem.

## Chat

`.vaogc` — version, queued, reclaimed, pending

## What it patches (Harmony)

1. `VAO.Finalize` — queue the undisposed VAO instead of leaking it
2. `ClientMain.ExecuteMainThreadTasks` — dispose a budgeted number of queued VAOs per frame on the GL thread (16 normally, 32 if pending >= 64, 64 if pending >= 256)
3. On a hitch (>= 80 ms) drain more of the queue on the render thread. No `GC.Collect`.
4. Writes `Logs/frameguard.json` off the render thread so metrics I/O cannot hitch the frame.

Does not change graphics settings. Does not run on the server. Safe for public multiplayer (same bucket as a texture pack).

1.1.4 wraps the Harmony Finalize prefix and drain postfix in outer try/catch so a dispose/queue failure cannot take the client down.

## Build

Windows, Vintage Story 1.22.x. The `.csproj` looks for game assemblies under `%APPDATA%\Vintagestory` (the default install).

```
dotnet build -c Release
```

Release zip is packed next to the project as `dist/vsvaogc_1.1.4.zip` (DLL + `modinfo.json` + pdb only).

## Related

- https://github.com/anegostudios/VintageStory-Issues/issues/8488
- https://github.com/anegostudios/VintageStory-Issues/issues/9712

MIT. Author: IllLeroySquad. Contributor: Fox.
