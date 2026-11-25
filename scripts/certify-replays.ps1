[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ProtocolDir,
    [Parameter(Mandatory = $true)]
    [string[]]$Maps,
    [string]$CorpusFile,
    [string]$CacheDir,
    [string]$JsonOut = "dist/replay-certification.json",
    [string]$MdOut = "dist/replay-certification.md",
    [ValidateRange(1, 64)]
    [int]$Concurrency = 24,
    [ValidateRange(0, [int]::MaxValue)]
    [int]$Limit = 0,
    [switch]$SaveCache
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Proj = Join-Path $RepoRoot "tools\ReplayCertifier\ReplayCertifier.csproj"

Write-Host "=== Building ReplayCertifier (Release) ===" -ForegroundColor Cyan
dotnet build $Proj -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { throw "ReplayCertifier build failed." }

$Exe = Join-Path $RepoRoot "tools\ReplayCertifier\bin\Release\net8.0\ReplayCertifier.exe"
$cliArgs = @("--protocol-dir", $ProtocolDir, "--concurrency", $Concurrency.ToString(), "--json-out", $JsonOut, "--md-out", $MdOut)
foreach ($entry in $Maps) {
    $literal = $entry.Trim()
    if (-not $literal) { continue }
    if (Test-Path -LiteralPath $literal -PathType Leaf) {
        $cliArgs += @("--maps", $literal)
    } else {
        foreach ($m in ($entry -split ',')) {
            $trimmed = $m.Trim()
            if ($trimmed) { $cliArgs += @("--maps", $trimmed) }
        }
    }
}
if ($CorpusFile) { $cliArgs += @("--corpus", $CorpusFile) }
if ($CacheDir) { $cliArgs += @("--cache-dir", $CacheDir) }
if ($PSBoundParameters.ContainsKey('Limit') -or $Limit -gt 0) { $cliArgs += @("--limit", $Limit.ToString()) }
if ($SaveCache) { $cliArgs += "--save-cache" }

Write-Host "=== Running Automated Replay Certification ===" -ForegroundColor Cyan
& $Exe @cliArgs
exit $LASTEXITCODE
