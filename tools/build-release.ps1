# Builds a release: runs the unit tests, publishes the app (trimmed, self-contained x64) to VidFlow-Release\,
# and compiles the installer to dist\VidFlow-Setup-<version>-x64.exe.
#   pwsh tools/build-release.ps1
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = ([xml](Get-Content (Join-Path $root 'VidFlow\VidFlow.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

Write-Host "== Strings"
& (Join-Path $PSScriptRoot 'gen-strings.ps1')

Write-Host "== Tests"
dotnet test --project (Join-Path $root 'VidFlow.Tests\VidFlow.Tests.csproj')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

Write-Host "== Publish $version"
$out = Join-Path $root 'VidFlow-Release'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish (Join-Path $root 'VidFlow\VidFlow.csproj') -c Release -p:Platform=x64 -o $out
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }

Write-Host "== Installer"
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }
# Antivirus real-time scanning can briefly lock a freshly written setup.exe; start from a clean output.
Get-ChildItem (Join-Path $root 'dist') -Filter '*.exe' -ErrorAction SilentlyContinue | Remove-Item -Force
& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\VidFlow.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }

Get-ChildItem (Join-Path $root 'dist') | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
