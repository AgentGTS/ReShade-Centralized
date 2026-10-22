# Validation

Verified locally on 2026-10-22: Release build with zero warnings/errors, whitespace formatting verification, and all 30 regression/Windows checks passed. Both official runtime installers were validated using local fixtures. Registry tests used disposable HKCU keys in both views. No live games were launched; the large online shader archive checks were not repeated.

Versioning update on 2026-10-22: app version `1.0.0-beta.1`, clean Release build, all 32 regression checks passed, and UI previews passed. The optional Windows registry check was not repeated for this change.

## Automated coverage

The dependency-free console suite creates its fixtures under the ignored `.validation/tests` directory. It never launches a game or installs the system Vulkan layer.

- INI section handling, duplicate-key replacement, custom settings, depth definitions, and escaped commas.
- Snapshot hashes, overwrite/create/delete rollback, pending-operation recovery, injected registry failures, and backup tampering.
- ZIP traversal, reserved names, alternate streams, case collisions, and preflight rejection.
- Official download selection, malformed installers and PE files, catalog fallback, repository aliases, and package dependencies.
- Unsupported rendering APIs are rejected before deployment modifies files.
- App release checks cover semantic version precedence, beta/stable filtering, draft and invalid-tag exclusion, pagination, missing releases, HTTP failures, and cancellation.
- Shader includes/textures, exclusion of executable add-ons, local modifications, obsolete owned files, cancellation, and legacy profile import.
- Official 6.8.0 payload extraction/identity checks, both PE architectures, and Vulkan manifests.
- Vulkan migration, external consumers, global build switching, reference counting, preservation of unrelated entries, and restoration using an in-memory registry backend.
- Real filesystem symlinks, repeated repair, DLL ownership, API switching, removal, locked runtime rollback, relocation, and restore.
- Optional Windows integration writes DWORD values under unique `HKCU\SOFTWARE\ReShadeCentralizedTests` subkeys in both registry views, then restores/removes those test keys. Production `Khronos\Vulkan` keys remain untouched.
- Optional online checks fetch and validate the current official runtimes and package trees from Standard, iMMERSE, FXShaders, RSRetroArch, Lilium, Warp-FX, Pirate Shaders, MLUT, MadCake, and Glamarye. Archive/layout validation does not compile shaders.

## UI previews

The dark-theme update passed a clean Release build, 29 regression checks, formatting verification, and the UI preview checks below on 2026-10-22. The optional registry test was not repeated for this UI change.

The preview mode renders all four pages at normal and minimum window sizes, selected game rows, and setup/install/restore dialogs. It checks command clipping, package filtering, required-package selection, and Vulkan's disabled per-game build selector. Example game rows appear only in preview images; preview mode does not load manager state, deploy files, or register Vulkan layers.

```powershell
dotnet 'bin/Release/net10.0-windows/win-x64/ReShade Centralized.dll' --smoke-test --ui-gallery .validation/dark-ui
```

Native title bars and common file dialogs use the theming available on the host Windows version. `DrawToBitmap` previews do not capture DWM-composited title-bar styling. High-contrast mode and changes between monitors with different DPI settings still require interactive checks.

## Live acceptance still required

Use disposable profiles or backed-up single-player game installations. For each of Direct3D 9, DXGI, OpenGL, and Vulkan, confirm the overlay reports the expected runtime version, enable an effect, save/reload a preset, and take a screenshot into the central screenshot directory.

1. Install two games, update the central runtime once, and confirm both load the updated version on their next launch.
2. For Vulkan, test ordinary launches for x64 and x86 where a compatible game is available. Verify separate presets and caches, switch the global build, and confirm an application with no ReShade configuration does not initialize ReShade.
3. Import an existing official Vulkan registration, retain its application records, and confirm those games still load. Restore the migration backup and verify the previous layer registration and runtime are back.
4. Check shader compilation and textures for the selected packages on each relevant graphics API. Some legacy effects and depth/HDR shaders require game-specific configuration.

GPU rendering, shader compilation, and multiplayer compatibility cannot be inferred from filesystem or registry tests.
