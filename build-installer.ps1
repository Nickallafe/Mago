#Requires -Version 5.1
<#
.SYNOPSIS
  Publica o Mago da Performance (C#/WPF) e gera o instalador MagoSetup-<versao>.exe.

.DESCRIPTION
  Etapas:
    1. dotnet publish (self-contained, single file) -> publish\MagoDaPerformance.exe
    2. ISCC.exe MagoSetup.iss -> MagoSetup-<versao>.exe (atalhos, desinstalador,
       entrada em Aplicativos e recursos)
    3. grava MagoSetup-<versao>.exe.sha256

  Requisitos: .NET SDK 10 e Inno Setup 6 (winget install JRSoftware.InnoSetup).
  O PC do utilizador NAO precisa de .NET: o executavel leva o runtime embutido.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Version 0.2.0

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Version 0.2.0 -SkipExe
#>
[CmdletBinding()]
param(
    [string] $Version = '0.2.0',
    [switch] $SkipExe
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
Set-Location -LiteralPath $root

$projectDir = Join-Path $root 'MagoDaPerformance'
$distDir = Join-Path $root 'publish'
$exePath = Join-Path $distDir 'MagoDaPerformance.exe'
$issFile = Join-Path $root 'MagoSetup.iss'
$project = Join-Path $projectDir 'MagoDaPerformance.csproj'

if (-not (Test-Path -LiteralPath $project)) { throw "Projeto C# nao encontrado: $project" }
if (-not (Test-Path -LiteralPath $issFile)) { throw "MagoSetup.iss nao encontrado em $root." }

# 0. Localizar o dotnet (PATH ou instalacao do usuario)
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    $candidate = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $candidate) { $dotnet = $candidate }
}
if (-not $dotnet -and -not $SkipExe) { throw "dotnet nao encontrado. Instale o .NET SDK 10." }

# 1. Executavel self-contained
if (-not $SkipExe) {
    Write-Host "Publicando app C# (self-contained, single file)..." -ForegroundColor Cyan
    $log = Join-Path $root 'dotnet-publish.log'
    cmd /c "`"$dotnet`" publish `"$project`" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o `"$distDir`" > `"$log`" 2>&1"
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        Get-Content -LiteralPath $log -Tail 25 | Write-Host
        throw "dotnet publish falhou (exit $code). Detalhes em $log"
    }
    Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
}
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "MagoDaPerformance.exe nao encontrado em $distDir. Rode sem -SkipExe."
}

# 2. Instalador
$iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    foreach ($cand in @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
    )) {
        if ($cand -and (Test-Path -LiteralPath $cand)) { $iscc = $cand; break }
    }
}
if (-not $iscc) { throw "ISCC.exe (Inno Setup) nao encontrado. Instale com: winget install JRSoftware.InnoSetup" }

Write-Host "Compilando instalador $Version ..." -ForegroundColor Cyan
& $iscc "/DMyVersion=$Version" "/DMySourceDir=$distDir" "/DMyOutputDir=$root" '/Qp' $issFile
if ($LASTEXITCODE -ne 0) { throw "ISCC falhou (exit $LASTEXITCODE)." }

$setup = Join-Path $root "MagoSetup-$Version.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "Setup nao gerado: $setup" }

# 3. Hash
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  MagoSetup-$Version.exe" | Set-Content -LiteralPath "$setup.sha256" -Encoding ASCII

Write-Host ""
Write-Host "  Exe:   $exePath" -ForegroundColor Green
Write-Host "  Setup: $setup" -ForegroundColor Green
Write-Host "  SHA256: $hash" -ForegroundColor DarkGray
