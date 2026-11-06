#Requires -Version 5.1
<#
.SYNOPSIS
  Gera o MagoDaPerformance.exe (PyInstaller) e o instalador MagoSetup-<versao>.exe.

.DESCRIPTION
  Etapas:
    1. pyinstaller mago.spec       -> dist\MagoDaPerformance.exe (sem Python no destino)
    2. ISCC.exe MagoSetup.iss      -> MagoSetup-<versao>.exe (atalhos, desinstalador,
                                      entrada em Aplicativos e recursos)
    3. grava MagoSetup-<versao>.exe.sha256

  Precisa de: Python 3.8+ com PyInstaller (pip install pyinstaller) e
  Inno Setup 6 (winget install JRSoftware.InnoSetup).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Version 0.1.2

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build-installer.ps1 -Version 0.1.2 -SkipExe
#>
[CmdletBinding()]
param(
    [string] $Version = '0.1.2',
    [switch] $SkipExe,
    [switch] $KeepBuild
)

$ErrorActionPreference = 'Stop'
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
Set-Location -LiteralPath $root

$distDir = Join-Path $root 'dist'
$exePath = Join-Path $distDir 'MagoDaPerformance.exe'
$issFile = Join-Path $root 'MagoSetup.iss'

if (-not (Test-Path -LiteralPath $issFile)) { throw "MagoSetup.iss nao encontrado em $root." }

# 1. Executavel standalone
if (-not $SkipExe) {
    Write-Host "Gerando $exePath ..." -ForegroundColor Cyan
    Push-Location $root
    try {
        pyinstaller --noconfirm --clean mago.spec 2>&1 | Tee-Object -Variable pyOut | Out-Null
        if ($LASTEXITCODE -ne 0) { $pyOut | Select-Object -Last 25 | Write-Host; throw "PyInstaller falhou (exit $LASTEXITCODE)." }
    } finally {
        Pop-Location
    }
}
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "MagoDaPerformance.exe nao encontrado em $distDir. Rode sem -SkipExe ou gere o exe antes."
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

if (-not $KeepBuild) {
    foreach ($p in @('build', '__pycache__')) {
        $full = Join-Path $root $p
        if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

Write-Host ""
Write-Host "  Exe:   $exePath" -ForegroundColor Green
Write-Host "  Setup: $setup" -ForegroundColor Green
Write-Host "  SHA256: $hash" -ForegroundColor DarkGray
