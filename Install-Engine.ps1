param([string]$Archive)

$ErrorActionPreference = 'Stop'
$directory = Join-Path $PSScriptRoot 'engine'
$engine = Join-Path $directory 'AstralSplit.Router.exe'
$engineSha256 = '8f30682ee198855440d2a7fe070d5d3653793b2d01bb8c85eea88f93bbe7c8b9'
$archiveSha256 = '99185d75d314a07dbba4a130c8ad986cbeb629a07927975636b341ca61f991df'
$asset = 'sing-box-1.14.4-windows-amd64'

if ([IO.File]::Exists($engine)) {
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $engine).Hash -ne $engineSha256) {
        throw 'A different engine is already present. Move it aside and run setup again.'
    }
    Write-Output 'The verified sing-box engine is already installed.'
    return
}

[void][IO.Directory]::CreateDirectory($directory)
$download = $null
$pending = Join-Path $directory ('.engine-' + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    if (!$Archive) {
        $download = [IO.Path]::GetTempFileName()
        $Archive = $download
        $url = "https://github.com/SagerNet/sing-box/releases/download/v1.14.4/$asset.zip"
        Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 60 -OutFile $download
    }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $Archive).Hash -ne $archiveSha256) {
        throw 'The sing-box archive did not match the official SHA-256.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $bundle = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
    try {
        $entry = $bundle.GetEntry("$asset/sing-box.exe")
        if (!$entry -or $entry.Length -ne 82074624) { throw 'Unexpected official archive layout.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $pending, $false)
    } finally {
        $bundle.Dispose()
    }
    if ((Get-FileHash -Algorithm SHA256 -LiteralPath $pending).Hash -ne $engineSha256) {
        throw 'The sing-box executable did not match the required SHA-256.'
    }
    [IO.File]::Move($pending, $engine)
    Write-Output 'Verified sing-box 1.14.4 is installed. You can now open AstralSplit.exe.'
} finally {
    [IO.File]::Delete($pending)
    if ($download) { [IO.File]::Delete($download) }
}
