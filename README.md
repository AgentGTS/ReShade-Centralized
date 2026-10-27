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
dotnet run --project Tests/ReShadeCentralized.Tests.csproj -c Release
powershell -NoProfile -ExecutionPolicy Bypass -File Publish.ps1
```

Release files are written to `artifacts`.

## Credits and license

- [OopyDoopy](https://github.com/OopyDoopy/ReShade-Centralized): original ReShade Centralized.
- [crosire and contributors](https://github.com/crosire/reshade): ReShade and its native behavior references; see the full [BSD-3-Clause notice](https://github.com/crosire/reshade/blob/v6.8.0/LICENSE.md).
- The [upstream shader catalog](https://github.com/crosire/reshade-shaders/blob/list/EffectPackages.ini) and [compatibility catalog](https://github.com/crosire/reshade-shaders/blob/list/Compatibility.ini) are downloaded directly and cached in the user's central storage. Upstream catalog snapshots are excluded from this repository and release; catalog licensing limits shown in Credits remain separate from this application's license.
- Shader package authors.
- [.NET contributors](https://github.com/dotnet): WinForms and the bundled runtime.

[MIT license](LICENSE.md) for this project and its retained original-project components. Third-party material retains its own terms. Open **Credits** in the app to read the application license, ReShade notice, catalog provenance, full .NET release notices, and retained notices from installed shader packages. Releases also include `THIRD-PARTY-NOTICES.txt`, with the same complete runtime-pack notices embedded in the app. Root/nested package license and attribution documents are retained during installation; reapply older packages to collect documents missing from earlier installations.
