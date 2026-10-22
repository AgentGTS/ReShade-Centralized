param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts\win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
dotnet publish (Join-Path $repo 'ReShade Centralized.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $repo 'README.md'), (Join-Path $repo 'LICENSE.md') -Destination $OutputDirectory
$docs = Join-Path $OutputDirectory 'docs'
New-Item -ItemType Directory -Path $docs -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'docs\VALIDATION.md') -Destination $docs
$assets = Get-Content -LiteralPath (Join-Path $repo 'obj\project.assets.json') -Raw | ConvertFrom-Json
$noticeParts = New-Object 'System.Collections.Generic.List[string]'
$dependencies = $assets.project.frameworks.PSObject.Properties.Value.downloadDependencies |
    Where-Object { $_.name -in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64') } |
    Sort-Object name -Unique
foreach ($dependency in $dependencies) {
    $version = $dependency.version.Trim([char[]]'[]').Split(',')[0].Trim()
    $relative = $dependency.name.ToLowerInvariant() + '\' + $version
    $foundLicense = $false
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $package = Join-Path $folder $relative
        if (-not (Test-Path -LiteralPath $package)) { continue }
        foreach ($name in @('LICENSE', 'LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
            $notice = Join-Path $package $name
            if (Test-Path -LiteralPath $notice) {
                $noticeParts.Add("=== $($dependency.name) $version / $name ===`n" + [IO.File]::ReadAllText($notice))
                if ($name -like 'LICENSE*') { $foundLicense = $true }
            }
        }
        break
    }
    if (-not $foundLicense) { throw "Runtime license missing for $($dependency.name)." }
}
if ($noticeParts.Count -eq 0) { throw 'No self-contained runtime license notices were found.' }
$thirdParty = Join-Path $OutputDirectory 'THIRD-PARTY-NOTICES.txt'
[IO.File]::WriteAllText($thirdParty, [string]::Join("`n`n", $noticeParts))
$binary = Join-Path $OutputDirectory 'ReShade Centralized.exe'
$appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($binary).ProductVersion.Split('+')[0]
$hash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'SHA256SUMS.txt'), "$hash  ReShade Centralized.exe`n")
$zip = Join-Path (Split-Path -Parent $OutputDirectory) "ReShade-Centralized-$appVersion-win-x64.zip"
# Package only release files, even when a prior executable has already created manager data.
Compress-Archive -LiteralPath $binary, (Join-Path $OutputDirectory 'README.md'), (Join-Path $OutputDirectory 'LICENSE.md'), `
    (Join-Path $OutputDirectory 'SHA256SUMS.txt'), $thirdParty, $docs -DestinationPath $zip -Force
Write-Host "Release: $zip"
Write-Host "SHA-256: $hash"
