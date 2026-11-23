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
$stageRoot = Join-Path $root "dist\stage"
$stageModDir = Join-Path $stageRoot $modId
$zipPath = Join-Path $root "dist\$modId-$version.zip"

if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stageModDir | Out-Null

Copy-Item (Join-Path $binDir "$modId.dll") $stageModDir -Force
Copy-Item (Join-Path $binDir "$modId.json") $stageModDir -Force
Copy-Item (Join-Path $binDir "config.json") $stageModDir -Force

if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -File $stageModDir) {
        $entryName = "$modId/$($file.Name)"
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $archive.Dispose()
}

Write-Host "=== Package created ==="
Write-Host $zipPath
Get-Item $zipPath | Select-Object Name, Length
