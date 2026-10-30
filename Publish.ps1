$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$release = Join-Path $repo 'release'
$project = Join-Path $repo 'ReShade Centralized.csproj'
dotnet restore $project -r win-x64 -p:SelfContained=true --nologo
if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
$assets = Get-Content -LiteralPath (Join-Path $repo 'obj\project.assets.json') -Raw | ConvertFrom-Json
$noticeParts = New-Object 'System.Collections.Generic.List[string]'
$noticeParts.Add("ReShade Centralized - third-party notices`n" +
    "Application code: MIT; see LICENSE.md. Third-party material retains its own terms.`n")
foreach ($name in @('ReShade-BSD-3-Clause.txt', 'Catalogs-and-Shaders.txt')) {
    $noticeParts.Add([IO.File]::ReadAllText((Join-Path $repo "Notices\$name")))
}
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
if (-not $dependencies) { throw 'No self-contained runtime license dependencies were found.' }
$embeddedNotices = Join-Path $repo 'obj\ReleaseThirdPartyNotices.txt'
[IO.File]::WriteAllText($embeddedNotices, [string]::Join("`n`n", $noticeParts))
dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false "-p:GeneratedThirdPartyNotices=$embeddedNotices" -o $release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item -LiteralPath (Join-Path $repo 'README.md'), (Join-Path $repo 'LICENSE.md') -Destination $release
Copy-Item -LiteralPath $embeddedNotices -Destination (Join-Path $release 'THIRD-PARTY-NOTICES.txt')
$binary = Join-Path $release 'ReShade Centralized.exe'
$hash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS.txt'), "$hash  ReShade Centralized.exe`n")
Write-Host "Release: $release"
Write-Host "SHA-256: $hash"
