# Client MeshRef Leak Fix (vsvaogc)

Client-only Vintage Story mod. The game sometimes forgets to Dispose MeshRefs / OpenGL VAOs, so VRAM fills up over a long session and FPS slides. This catches those on Finalize and actually frees them on the render thread.

ModDB: https://mods.vintagestory.at/vsvaogc

## Install

Use the dll-only zip from ModDB. Don't drop this source folder into Mods. Vintage Story will try to compile the .cs files and skip the actual mod.

## Chat

`.vaogc` shows version, queued, reclaimed, pending.

## What it patches

1. `VAO.Finalize` queues the undisposed VAO instead of leaking it
2. `ClientMain.ExecuteMainThreadTasks` disposes a few queued VAOs each frame on the GL thread (16 normally, 32 if pending is 64+, 64 if pending is 256+)
3. If a frame hitch hits 80ms, drain more of the queue. No `GC.Collect`.
4. Writes `Logs/frameguard.json` off the render thread so metrics I/O doesn't hitch the frame.

Doesn't change graphics settings. Doesn't run on the server. Fine on public multiplayer.

1.1.4 wraps the Harmony hooks in try/catch so a dispose failure can't take the client down.

## Build

Windows, Vintage Story 1.22.x. The csproj looks for game assemblies under `%APPDATA%\Vintagestory`.

```
dotnet build -c Release
```

Release zip lands at `dist/vsvaogc_1.1.4.zip` (dll + modinfo.json + pdb only).

## Related

- https://github.com/anegostudios/VintageStory-Issues/issues/8488
- https://github.com/anegostudios/VintageStory-Issues/issues/9712

MIT. Author: IllLeroySquad. Contributor: Fox.
