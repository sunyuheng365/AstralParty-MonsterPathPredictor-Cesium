param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$modProj = Join-Path $root "src\MonsterPathPredictor\MonsterPathPredictor.csproj"
$testProj = Join-Path $root "tests\MonsterPathPredictor.Tests\MonsterPathPredictor.Tests.csproj"
$manifestPath = Join-Path $root "src\MonsterPathPredictor\MonsterPathPredictor.json"

$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$modId = $manifest.id
$version = $manifest.version

Write-Host "=== Running unit tests ==="
dotnet test $testProj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Unit tests failed." }

Write-Host "=== Building $modId v$version ($Configuration) ==="
dotnet build $modProj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Mod build failed." }

$binDir = Join-Path $root "src\MonsterPathPredictor\bin\$Configuration\netstandard2.0"
$distDir = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null

$stageRoot = Join-Path $distDir ("stage-" + [Guid]::NewGuid().ToString("N").Substring(0, 12))
$stageModDir = Join-Path $stageRoot $modId
$zipPath = Join-Path $distDir "$modId-$version.zip"
$tmpZipPath = Join-Path $distDir ("$modId-$version.zip." + [Guid]::NewGuid().ToString("N").Substring(0, 8) + ".tmp")
$bakZipPath = Join-Path $distDir ("$modId-$version.zip." + [Guid]::NewGuid().ToString("N").Substring(0, 8) + ".bak")

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

try {
    New-Item -ItemType Directory -Force -Path $stageModDir | Out-Null

    $requiredFiles = @("$modId.dll", "$modId.json", "config.json")
    foreach ($rf in $requiredFiles) {
        $src = Join-Path $binDir $rf
        if (-not (Test-Path $src)) { throw "Required package file missing: $src" }
        Copy-Item $src $stageModDir -Force
    }

    $archive = [System.IO.Compression.ZipFile]::Open($tmpZipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -File $stageModDir) {
            $entryName = "$modId/$($file.Name)"
            [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally {
        $archive.Dispose()
    }

    # Verify archive integrity and all 3 required entries before replacing final ZIP
    $verify = [System.IO.Compression.ZipFile]::OpenRead($tmpZipPath)
    try {
        $entryNames = $verify.Entries | ForEach-Object { $_.FullName }
        foreach ($rf in $requiredFiles) {
            $expected = "$modId/$rf"
            if ($entryNames -notcontains $expected) { throw "Package verification failed: missing $expected" }
        }
    } finally {
        $verify.Dispose()
    }

    # Atomic same-volume publish without a destination-missing deletion window
    if ([System.IO.File]::Exists($zipPath)) {
        [System.IO.File]::Replace($tmpZipPath, $zipPath, $bakZipPath, $true)
    } else {
        try {
            [System.IO.File]::Move($tmpZipPath, $zipPath)
        } catch [System.IO.IOException] {
            if ([System.IO.File]::Exists($zipPath)) {
                [System.IO.File]::Replace($tmpZipPath, $zipPath, $bakZipPath, $true)
            } else {
                throw
            }
        }
    }
} finally {
    if (Test-Path $tmpZipPath) { Remove-Item $tmpZipPath -Force -ErrorAction SilentlyContinue }
    if (Test-Path $bakZipPath) { Remove-Item $bakZipPath -Force -ErrorAction SilentlyContinue }
    if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host "=== Package created ==="
Write-Host $zipPath
Get-Item $zipPath | Select-Object Name, Length
