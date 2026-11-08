# Builds a ready-to-run PathFinder.exe for Windows: one self-contained file, no .NET install needed.
# Usage (from anywhere):  powershell -ExecutionPolicy Bypass -File tools\publish.ps1
# Output: publish\PathFinder.exe (git-ignored). Copy that one file to any Windows laptop and run it.

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\PathFinder\PathFinder.csproj'
$output = Join-Path $repo 'publish'

dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }
# Skia/HarfBuzz ship debug symbols next to the exe; players don't need them.
Remove-Item (Join-Path $output '*.pdb') -ErrorAction SilentlyContinue

$exe = Join-Path $output 'PathFinder.exe'
if (-not (Test-Path $exe)) { throw "Publish finished but $exe is missing" }
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Ready to run: $exe ($sizeMb MB)" -ForegroundColor Green
