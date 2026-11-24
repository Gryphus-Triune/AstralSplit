param([string]$Zig='zig')
$ErrorActionPreference='Stop'
$source=Join-Path $PSScriptRoot 'ParticleDevice.cpp'
$dll=Join-Path $PSScriptRoot 'AstralSplit.Graphics.dll'
& $Zig c++ -target x86_64-windows-gnu -shared -std=c++17 -O2 -s -fno-exceptions -fno-rtti $source -ld3d11 -ld2d1 -ld3d9 -ldxgi -ldcomp -lavrt -luser32 -o $dll
if($LASTEXITCODE -ne 0){throw 'The Direct3D particle renderer build failed.'}
