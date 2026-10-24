@echo off
setlocal
set "RESHADE_DEBUG_SCRIPT=%~f0"
powershell.exe -NoProfile -Command "if (([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 0 }; try { Start-Process -FilePath $env:ComSpec -ArgumentList ('/d /c ' + [char]34 + [char]34 + $env:RESHADE_DEBUG_SCRIPT + [char]34 + [char]34) -WorkingDirectory (Split-Path -LiteralPath $env:RESHADE_DEBUG_SCRIPT) -Verb RunAs -ErrorAction Stop; exit 10 } catch { Write-Host ('Could not start elevated Debug: ' + $_.Exception.Message); exit 1 }"
if errorlevel 10 exit /b 0
if errorlevel 1 exit /b 1

pushd "%~dp0"
if errorlevel 1 goto failure

where dotnet >nul 2>&1
if errorlevel 1 (
    echo Install the .NET 10 SDK, then try again.
    goto failure
)

dotnet build "ReShade Centralized.csproj" --configuration Debug
if errorlevel 1 (
    echo Debug build failed. The app was not started. See the errors above.
    goto failure
)

echo Starting Debug. Activity messages will appear below.
"%~dp0bin\Debug\net10.0-windows\win-x64\ReShade Centralized.exe"
set "appResult=%errorlevel%"
popd
exit /b %appResult%

:failure
echo Build stopped. Press any key to close this window.
pause >nul
popd
exit /b 1
