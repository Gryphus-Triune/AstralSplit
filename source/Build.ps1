param(
    [string]$Output = (Join-Path $PSScriptRoot 'bin\AstralSplit.exe'),
    [string]$Compiler
)

$ErrorActionPreference = 'Stop'
if (!$Compiler) {
    $candidatePaths = @(
        "$env:ProgramFiles\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe"
    )
    $Compiler = (Get-Item $candidatePaths -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
    if (!$Compiler) { throw 'Build requires Roslyn csc from Visual Studio or Build Tools.' }
}
$framework = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies\Microsoft\Framework\.NETFramework'
$refs = Get-ChildItem -LiteralPath $framework -Directory | Where-Object {
    $_.Name -match '^v4\.(7|8)' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'System.Xaml.dll'))
} | Sort-Object Name -Descending | Select-Object -First 1
if (!$refs) { throw 'Build requires .NET Framework 4.7 or later reference assemblies with WPF.' }

$destination = [IO.Path]::GetFullPath($Output)
[void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
$referenceNames = @('mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.Xaml', 'WindowsBase',
    'PresentationCore', 'PresentationFramework', 'System.Web.Extensions','UIAutomationClient','UIAutomationTypes')
$referenceArgs = foreach ($name in $referenceNames) { '/reference:' + (Join-Path $refs.FullName "$name.dll") }
$sourceArgs = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' | Select-Object -ExpandProperty FullName
$icon = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'AstralSplit.ico')) { '/win32icon:' + (Join-Path $PSScriptRoot 'AstralSplit.ico') }
$buildArgs = @('/nologo', '/nostdlib+', '/target:winexe', '/platform:x64', '/optimize+', '/unsafe+', '/warn:4', '/codepage:65001',
    '/main:AstralSplit.App', "/out:$destination", "/win32manifest:$PSScriptRoot\App.manifest",
    "/resource:$PSScriptRoot\Main.xaml,AstralSplit.Main.xaml")
if ($icon) { $buildArgs += $icon }
foreach ($module in 'Galaxy.cubin','Galaxy.ptx') {
    $buildArgs += ('/resource:' + (Join-Path $PSScriptRoot $module) + ',AstralSplit.' + $module)
}
foreach ($art in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'StellarArt') -File | Where-Object { $_.Extension -in '.jpg','.png' }) {
    $buildArgs += ('/resource:' + $art.FullName + ',AstralSplit.StellarArt.' + $art.Name)
}
$shaders=@{main='LiquidGlass';projected='ProjectedGlass';stellarLens='StellarLens';stellarFull='StellarFull';moteVS='MoteVertex';motePS='MotePixel';dustVS='DustVertex';dustPS='DustPixel';depositPS='DustExposure';nebulaPS='Volume'}
$shaders += @{main2='LiquidGlass2';projected2='ProjectedGlass2';stellarLens2='StellarLens2';stellarFull2='StellarFull2';bevelField='BevelField'}
foreach($i in 'nativeMoteVS','nativeFieldVS','nativeDustVS','solarVS','solarPS','sceneFilmPS','filmCometPS','filmPulsarPS','filmNovaPS','filmDiamondPS','launchVS','launchPS','meteorVS','meteorPS'){$shaders[$i]=$i}
$shaders.windowSceneVS='WindowSceneVertex'
$shaders.windowScenePS='WindowScenePixel'
$shaders.volumeVS='CompositionVertex'
$shaders.volumePixel='CompositionVolume'
$shaders.captionGlass='CaptionGlass'
foreach($orbitEntry in 'orbitVS','orbitGS','orbitMask','orbitQuad','orbitInk'){$shaders[$orbitEntry]=$orbitEntry}
$shaders.magneticMap='MagneticMap'
$shaders.magneticLookup='MagneticLookup'
$shaders.magneticVS='MagneticVertex'
$shaders.magneticPS='MagneticPixel'
$shaders.magneticOptimizedPS='MagneticOptimizedPixel'
$shaders.sceneLens='SceneLens'
$shaders.stellarStandby='StellarStandby'
$shaders.maskedProjection='MaskedProjection'
$shaders.accretionProjection='AccretionProjection'
$temporary=@()
try {
    foreach($entry in $shaders.Keys | Sort-Object){
        $file=[IO.Path]::GetTempFileName()
        $temporary+=$file
        & (Join-Path $PSScriptRoot 'CompileGlass.ps1') -Output $file -Entry $entry
        $buildArgs += "/resource:$file,AstralSplit.$($shaders[$entry]).ps"
    }
    & $Compiler @buildArgs @referenceArgs @sourceArgs
    if ($LASTEXITCODE -ne 0) { throw 'Compilation failed.' }
} finally { foreach($file in $temporary){[IO.File]::Delete($file)} }
Write-Output $destination
$native=Join-Path $PSScriptRoot 'AstralSplit.Graphics.dll'
$runtime=Join-Path ([IO.Path]::GetDirectoryName($destination)) 'AstralSplit.Graphics.dll'
if([IO.Path]::GetFullPath($native) -ne [IO.Path]::GetFullPath($runtime)){[IO.File]::Copy($native,$runtime,$true)}
