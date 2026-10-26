# Builds a release: runs the unit tests, publishes the app (trimmed, self-contained) for each architecture to
# VidFlow-Release\ (x64) and VidFlow-Release-arm64\, and compiles the installers to dist\VidFlow-Setup-<version>-<arch>.exe
# with their update manifests (update.json for x64, update-arm64.json for ARM64).
#   pwsh tools/build-release.ps1                 # both
#   pwsh tools/build-release.ps1 -Arch arm64     # just one
param([ValidateSet('x64', 'arm64')][string[]]$Arch = @('x64', 'arm64'))
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = ([xml](Get-Content (Join-Path $root 'VidFlow\VidFlow.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

Write-Host "== Strings"
& (Join-Path $PSScriptRoot 'gen-strings.ps1')

Write-Host "== Tests"
dotnet test --project (Join-Path $root 'VidFlow.Tests\VidFlow.Tests.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
$notesFile = Join-Path $root "docs\release-notes\$version.md"
$notes = if (Test-Path $notesFile) { Get-Content $notesFile -Raw -Encoding UTF8 } else { '' }

foreach ($a in $Arch) {
    $platform = if ($a -eq 'arm64') { 'ARM64' } else { 'x64' }
    $out = Join-Path $root $(if ($a -eq 'arm64') { 'VidFlow-Release-arm64' } else { 'VidFlow-Release' })

    Write-Host "== Publish $version $a"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet publish (Join-Path $root 'VidFlow\VidFlow.csproj') -c Release -p:Platform=$platform -r "win-$a" -o $out
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($a)" }

    Write-Host "== Installer $a"
    # Antivirus real-time scanning can briefly lock a freshly written setup.exe; start from a clean output.
    Get-ChildItem (Join-Path $root 'dist') -Filter "*-$a.exe" -ErrorAction SilentlyContinue | Remove-Item -Force
    & $iscc "/DAppVersion=$version" "/DArch=$a" (Join-Path $root 'installer\VidFlow.iss')
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed ($a)" }

    Write-Host "== Update manifest $a"
    # Attached to the GitHub release next to the installer; the app reads
    # https://github.com/Magicherry/VidFlow/releases/latest/download/<manifest> for its own architecture.
    $setup = Get-Item (Join-Path $root "dist\VidFlow-Setup-$version-$a.exe")
    $manifest = [ordered]@{
        Version   = $version
        Url       = "https://github.com/Magicherry/VidFlow/releases/download/v$version/$($setup.Name)"
        Sha256    = (Get-FileHash $setup.FullName -Algorithm SHA256).Hash
        Size      = $setup.Length
        Notes     = $notes
        Published = (Get-Date).ToUniversalTime().ToString('o')
    }
    $name = if ($a -eq 'arm64') { 'update-arm64.json' } else { 'update.json' }
    $manifest | ConvertTo-Json | Set-Content (Join-Path $root "dist\$name") -Encoding utf8
}

Get-ChildItem (Join-Path $root 'dist') | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
