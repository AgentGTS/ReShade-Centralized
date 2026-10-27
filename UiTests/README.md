# Optional UI checks

Windows and the .NET 10 SDK are required. Run from the repository root in an interactive desktop session.
The runner references the actual app and Core assemblies. It is excluded from the main solution and publishing,
so stale checks cannot break normal app builds. Update checks when running them after relevant UI changes.

```powershell
# Quick shader-selection and button-state checks
dotnet run --project UiTests/ReShadeCentralized.UiTests.csproj -c Release

# Full UI gallery, loading/first-use checks, and isolated shutdown probes
dotnet run --project UiTests/ReShadeCentralized.UiTests.csproj -c Release -- --ui-gallery .validation/ui-gallery-new

# Screenshot of the main window
dotnet run --project UiTests/ReShadeCentralized.UiTests.csproj -c Release -- --screenshot .validation/main-window.png
```

Choose a new gallery directory for each full run; exit probes require fresh marked fixture directories.
The runner uses disposable storage, simulated downloads and registry fixtures for mutation checks.
It does not install into real games or change real Vulkan registrations. Exit checks launch disposable copies
of this runner and deliberately terminate only those processes to verify recovery.

The production executable no longer accepts preview/smoke-test commands. Test entry points, fixture data,
screenshots and shutdown-probe launching are contained in this project. It retains access to app internals
through `InternalsVisibleTo`; the app has no dependency on the runner.
