# ReShade Centralized

Shared ReShade runtimes and shaders, with per-game presets. Windows 10/11 x64.

## Install and use

1. Extract the release and run **ReShade Centralized.exe** as administrator. .NET is included.
2. Choose central storage, then **Runtime & storage > Update ReShade**.
3. In **Shader packages**, choose packages and select **Install / update selected**.
4. Choose **Games > Install game...**. Select the game executable, API, and build.
5. Launch the game; choose effects and save a preset in ReShade's overlay.

Close games before updates, relocation, or restore. Keep `ReShadeCentralized.json`, `Backups`, and central storage when updating the app.

For errors, check **Activity**. Press **F1** for [usage and troubleshooting](Assets/Help.txt).

## Build and test

Requires the .NET 10 SDK on Windows.

```powershell
dotnet build 'ReShade Centralized.sln' -c Release
dotnet run --project tests/ReShadeCentralized.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/Publish.ps1
```

Release files are written to `artifacts`.

## Credits and license

- [OopyDoopy](https://github.com/OopyDoopy/ReShade-Centralized): original ReShade Centralized.
- [crosire and contributors](https://github.com/crosire/reshade): ReShade and the [official shader catalog](https://github.com/crosire/reshade-shaders/blob/list/EffectPackages.ini).
- Shader package authors.
- [.NET contributors](https://github.com/dotnet): WinForms and the bundled runtime.

[MIT license](LICENSE.md). Third-party licenses apply; see `THIRD-PARTY-NOTICES.txt` in the release.
