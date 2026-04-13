# Local Build Dependencies

This repository does not commit Fly Corp game DLLs, generated IL2CPP interop assemblies, BepInEx binaries, or a local .NET SDK.

To build locally, populate these folders yourself:

- `deps/bepinex/`
- `deps/flycorp-interop/`

Expected sources:

- `deps/bepinex/`: extract `BepInEx 6 Unity IL2CPP x64` here
- `deps/flycorp-interop/`: copy the generated interop DLLs from `Fly Corp/BepInEx/interop/` after launching the game once with BepInEx installed

At minimum, the project file expects:

- `deps/bepinex/BepInEx/core/BepInEx.Core.dll`
- `deps/bepinex/BepInEx/core/BepInEx.Unity.Common.dll`
- `deps/bepinex/BepInEx/core/BepInEx.Unity.IL2CPP.dll`
- `deps/bepinex/BepInEx/core/0Harmony.dll`
- `deps/bepinex/BepInEx/core/Il2CppInterop.Runtime.dll`
- `deps/flycorp-interop/Assembly-CSharp.dll`
- `deps/flycorp-interop/UnityEngine.CoreModule.dll`
- `deps/flycorp-interop/UnityEngine.UI.dll`
- `deps/flycorp-interop/Unity.TextMeshPro.dll`
- `deps/flycorp-interop/PathCreator.dll`
- `deps/flycorp-interop/Il2Cppmscorlib.dll`
- `deps/flycorp-interop/Il2CppSystem.dll`
- `deps/flycorp-interop/Il2CppSystem.Core.dll`
- `deps/flycorp-interop/UniTask.dll`

Placeholder files:

- `deps/bepinex/.gitkeep`
- `deps/flycorp-interop/.gitkeep`
