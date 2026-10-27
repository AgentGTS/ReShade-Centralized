# ReShade Centralized

Shared ReShade runtimes and shaders, with per-game presets. Windows 10/11 x64.

## Install and use

1. Extract the release and run **ReShade Centralized.exe** as administrator. .NET is included.
2. Keep the default central folder (`Documents\ReShade Centralized`) or choose a folder outside the app folder.
3. Choose **Runtime & storage > Update ReShade**.
4. In **Shader packages**, select packages and choose **Apply selection**.

For example, to install for a DirectX 11 game:

1. Choose **Games > Install game...** and select the game's executable.
2. Select **DXGI (DirectX 10/11/12)** and **Standard**, review the changes, then choose **Install**.
3. Launch the game and use ReShade's overlay to select effects and save a preset.

For a Vulkan game:

1. In **Runtime & storage**, choose **Apply / repair** to apply the Vulkan layer.
2. Choose **Games > Install game...** and select the game's executable.
3. Select **Vulkan**, review the changes, then choose **Install**.
4. Launch the game and use ReShade's overlay to select effects and save a preset.

Press **F1** or read [Help](Assets/Help.txt) for other APIs, add-ons, updates, removal and troubleshooting.

## Build and test

Requires Windows and the .NET 10 SDK.

### Debug

Double-click **BuildRunDebug.bat** to build and run in console with live logs.

### Release

Run these commands in PowerShell from the repository folder:

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
