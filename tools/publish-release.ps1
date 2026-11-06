# Builds the release package that VoxForge's auto-updater downloads:
#   publish\VoxForge-win-x64.zip and publish\VoxForge-win-x64.zip.sha256
# With -Upload, also creates the GitHub release vX.Y.Z (needs the GitHub CLI, logged in).
# Bump <Version> in src/VoxForge/VoxForge.csproj before each release.
param([switch]$Upload)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src\VoxForge\VoxForge.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $root 'publish'
$app = Join-Path $out 'app'
$zip = Join-Path $out 'VoxForge-win-x64.zip'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=false -o $app
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash
"$hash  VoxForge-win-x64.zip" | Set-Content -Encoding ascii "$zip.sha256"
Write-Host "VoxForge v$version packaged: $zip"

if ($Upload) {
    gh release create "v$version" $zip "$zip.sha256" --repo ArachnoMLK/VoxForge --title "VoxForge v$version" --generate-notes
    if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
}
