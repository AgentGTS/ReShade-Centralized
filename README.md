# ReShade Centralized

One place for ReShade runtimes and shaders, with separate settings and presets for each game. Runs on Windows 10/11 x64 and supports x86/x64 games using DirectX 9-12, OpenGL, and Vulkan.

## Install and use

1. Extract the release into a permanent folder and run **ReShade Centralized.exe** as administrator. The release includes .NET.
2. Choose your central folder. To import an older installation, select its `ReShadeCentralized.ini`.
3. Open **Runtime & storage** and choose **Update ReShade**.
4. Under **Shader packages**, select packages and choose **Install / update selected**.
5. Under **Games**, choose **Install to game** and select the game's executable, graphics API, and build.

For Vulkan, use **Apply / repair Vulkan**. Its build selector affects all games using the shared layer.

Use **Repair / edit selected** to change an installation, **Remove selected** to remove its managed links, and **Restore...** to undo changes. Presets are retained. Close affected games before updates, relocation, or restore.

Keep `ReShadeCentralized.json` and `Backups` beside the application when updating it. Imported profiles need their game executable selected before repair or relocation.

## Build and test

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build 'ReShade Centralized.sln' -c Release
dotnet run --project tests/ReShadeCentralized.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish.ps1
```

Release files are written to `artifacts`. See [validation](docs/VALIDATION.md) for optional tests and live-game checks.

## Credits and license

- [OopyDoopy](https://github.com/OopyDoopy/ReShade-Centralized): original ReShade Centralized.
- [AgentGTS](https://github.com/AgentGTS/ReShade-Centralized): fork development and modernization.
- [crosire and contributors](https://github.com/crosire/reshade): ReShade and the [official shader catalog](https://github.com/crosire/reshade-shaders/blob/list/EffectPackages.ini).
- Shader package authors: their shaders, includes, and textures.
- [.NET contributors](https://github.com/dotnet): WinForms and the bundled runtime.

This manager uses the [MIT license](LICENSE.md). ReShade, shader packages, and the .NET runtime retain their own licenses. Runtime notices are included in the release's `THIRD-PARTY-NOTICES.txt`.
