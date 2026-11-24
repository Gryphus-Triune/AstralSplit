param([Parameter(Mandatory)][string]$Output,[ValidateSet('main','projected','stellarLens','stellarStandby','depositPS','moteVS','motePS','dustVS','dustPS','magneticVS','magneticPS','magneticOptimizedPS','magneticLookup','magneticMap','nebulaPS','stellarFull','main2','projected2','stellarLens2','stellarFull2','bevelField','sceneLens','volumeVS','volumePixel','captionGlass','orbitVS','orbitGS','orbitMask','orbitQuad','orbitInk','maskedProjection','accretionProjection','windowSceneVS','windowScenePS','nativeMoteVS','nativeFieldVS','nativeDustVS','solarVS','solarPS','launchVS','launchPS','meteorVS','meteorPS')][string]$Entry='main')
$ErrorActionPreference = 'Stop'
if (-not ('AstralBuild.GlassShaderCompiler' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
namespace AstralBuild {
    public static class GlassShaderCompiler {
        [DllImport("d3dcompiler_47.dll", CharSet=CharSet.Ansi, ExactSpelling=true)]
        static extern int D3DCompile(byte[] source, UIntPtr length, string name, IntPtr macros, IntPtr include,
            string entry, string target, uint flags, uint unused, out IntPtr code, out IntPtr messages);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate IntPtr Address(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate UIntPtr Length(IntPtr self);
        static byte[] Bytes(IntPtr blob) {
            IntPtr table = Marshal.ReadIntPtr(blob);
            var ptr = (Address)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, 3 * IntPtr.Size), typeof(Address));
            var len = (Length)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(table, 4 * IntPtr.Size), typeof(Length));
            byte[] bytes = new byte[checked((int)len(blob).ToUInt64())];
            Marshal.Copy(ptr(blob), bytes, 0, bytes.Length);
            return bytes;
        }
        public static void Build(string input, string output, string entry, string target) {
            var source = File.ReadAllBytes(input);
            IntPtr code, messages;
            int hr = D3DCompile(source, (UIntPtr)source.Length, input, IntPtr.Zero, IntPtr.Zero, entry, target,
                1u << 15, 0, out code, out messages);
            try {
                if (hr < 0) throw new InvalidOperationException(messages == IntPtr.Zero ? "Shader compilation failed" :
                    System.Text.Encoding.UTF8.GetString(Bytes(messages)).TrimEnd('\0'));
                File.WriteAllBytes(output, Bytes(code));
            } finally {
                if (code != IntPtr.Zero) Marshal.Release(code);
                if (messages != IntPtr.Zero) Marshal.Release(messages);
            }
        }
    }
}
'@
}
$source,$target=switch ($Entry) {
    { $_ -in 'orbitVS','orbitQuad' } { 'OrbitalMesh.hlsl'; 'vs_4_0' }
    'orbitGS' { 'OrbitalMesh.hlsl'; 'gs_4_0' }
    { $_ -in 'orbitMask','orbitInk' } { 'OrbitalMesh.hlsl'; 'ps_4_0' }
    { $_ -in 'nativeMoteVS','nativeFieldVS','nativeDustVS' } { 'SceneParticles.hlsl'; 'vs_4_0' }
    'meteorVS' { 'Meteor.hlsl'; 'vs_4_0' }
    'meteorPS' { 'Meteor.hlsl'; 'ps_4_0' }
    'launchVS' { 'Launch.hlsl'; 'vs_4_0' }
    'launchPS' { 'Launch.hlsl'; 'ps_4_0' }
    'solarVS' { 'SceneSolar.hlsl'; 'vs_4_0' }
    'solarPS' { 'SceneSolar.hlsl'; 'ps_4_0' }
    'windowSceneVS' { 'WindowScene.hlsl'; 'vs_4_0' }
    'windowScenePS' { 'WindowScene.hlsl'; 'ps_4_0' }
    'moteVS' { 'ParticleBatch.hlsl'; 'vs_4_0' }
    'motePS' { 'ParticleBatch.hlsl'; 'ps_4_0' }
    'volumeVS' { 'CompositionVolume.hlsl'; 'vs_4_0' }
    'volumePixel' { 'CompositionVolume.hlsl'; 'ps_4_0' }
    'captionGlass' { 'CaptionGlass.hlsl'; 'ps_3_0' }
    'magneticVS' { 'MagneticField.hlsl'; 'vs_4_0' }
    { $_ -in 'magneticMap','magneticLookup','magneticOptimizedPS' } { 'MagneticField.hlsl'; 'ps_4_0' }
    'magneticPS' { 'ParticleBatch.hlsl'; 'ps_4_0' }
    'dustVS' { 'ParticleBatch.hlsl'; 'vs_4_0' }
    'dustPS' { 'ParticleBatch.hlsl'; 'ps_4_0' }
    'maskedProjection' { 'AccretionProjection.hlsl'; 'ps_2_0' }
    'accretionProjection' { 'AccretionProjection.hlsl'; 'ps_2_0' }
    'depositPS' { 'DepositMask.hlsl'; 'ps_2_0' }
    'nebulaPS' { 'Volume.hlsl'; 'ps_2_0' }
    { $_ -in 'main','projected','stellarLens','stellarFull','bevelField','sceneLens' } { 'LiquidGlass.hlsl'; 'ps_3_0' }
    default { 'LiquidGlass.hlsl'; 'ps_2_0' }
}
$inputFile=Join-Path $PSScriptRoot $source
if($source -eq 'WindowScene.hlsl'){
    # Compile the exact same optical equations into the WPF and native profiles.
    $shared=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'LiquidGlass.hlsl'))
    $shared=$shared.Substring($shared.IndexOf('float4 transmitted('))
    $shared=$shared.Replace(': COLOR',': COLOR').Replace(' : COLOR','').Replace(':COLOR','').Replace(': COLOR','').Replace(' : TEXCOORD','').Replace(':TEXCOORD','')
    $merged=[IO.File]::ReadAllText($inputFile).Replace('// OPTICAL_FUNCTIONS',$shared)
    $tmp=[IO.Path]::GetTempFileName()
    try{
        [IO.File]::WriteAllText($tmp,$merged,[Text.UTF8Encoding]::new($false))
        [AstralBuild.GlassShaderCompiler]::Build($tmp,[IO.Path]::GetFullPath($Output),$Entry,$target)
    }finally{[IO.File]::Delete($tmp)}
}else{
    [AstralBuild.GlassShaderCompiler]::Build($inputFile,[IO.Path]::GetFullPath($Output),$Entry,$target)
}
