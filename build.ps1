#Requires -Version 5.1
<#
    build.ps1 - compiles CpuHog.cs into bin\CpuHog.exe

    Usage:
        powershell -ExecutionPolicy Bypass -File build.ps1

    Uses the .NET Framework C# compiler that ships with Windows, so no SDK is
    required. AnyCPU output means the executable runs on the native ARM64 CLR
    on ARM machines (no x64 emulation overhead).
#>

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $root 'CpuHog.cs'
$outDir = Join-Path $root 'bin'
$outExe = Join-Path $outDir 'CpuHog.exe'

if (-not (Test-Path $src)) { throw "source not found: $src" }

$cscCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
)
$csc = $cscCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "csc.exe not found; .NET Framework 4.x is required" }

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "compiler : $csc"
Write-Host "source   : $src"
Write-Host "output   : $outExe"

& $csc /nologo /target:exe /platform:anycpu /optimize+ "/out:$outExe" $src
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

$size = [math]::Round((Get-Item $outExe).Length / 1KB, 1)
Write-Host "built    : $outExe ($size KB)"
