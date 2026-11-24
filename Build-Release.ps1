param(
    [string]$Zig = 'zig',
    [string]$Python = 'python',
    [string]$CudaBin
)

$ErrorActionPreference = 'Stop'
$dist = Join-Path $PSScriptRoot 'dist'
$stage = Join-Path $dist ('staging-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($stage)

foreach ($directory in 'source', 'engine', 'tools') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $directory) -Destination $stage -Recurse
}
Get-ChildItem -LiteralPath $PSScriptRoot -File | Where-Object {
    $_.Extension -in '.md', '.cmd' -or $_.Name -in 'Build-Release.ps1', 'Install-Engine.ps1', 'LICENSE', 'THIRD_PARTY_LICENSES.txt'
} | Copy-Item -Destination $stage

$stagedSource = Join-Path $stage 'source'
& (Join-Path $stagedSource 'CompileParticles.ps1') -Zig $Zig
$cudaArgs = @()
if ($CudaBin) { $cudaArgs = @('--bin', $CudaBin) }
& $Python (Join-Path $stagedSource 'CompileGalaxy.py') @cudaArgs
if ($LASTEXITCODE -ne 0) { throw 'CUDA module build failed.' }
& (Join-Path $stagedSource 'Build.ps1') -Output (Join-Path $stage 'AstralSplit.exe')
& $Python (Join-Path $stagedSource 'PackagePortable.py') $stage (Join-Path $dist 'AstralSplit-Windows-x64-2.0.0.zip')
if ($LASTEXITCODE -ne 0) { throw 'Portable release packaging failed.' }
