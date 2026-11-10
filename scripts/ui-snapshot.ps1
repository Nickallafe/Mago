#Requires -Version 5.1
<#
.SYNOPSIS
  Gera PNGs das paginas (painel, otimizar, jogos, rede, ferramentas) para revisao visual do layout.

.DESCRIPTION
  Corre o harness tests/MagoDaPerformance.UiSnapshots a 1280 px de largura e grava os
  ficheiros em ui-snapshots\. Precisa de sessao de desktop (WPF) e de um SDK .NET 10.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\scripts\ui-snapshot.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputDir = ''
)

$ErrorActionPreference = 'Stop'
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$root = Resolve-Path (Join-Path $scriptDir '..')
if (-not $OutputDir) { $OutputDir = Join-Path $root 'ui-snapshots' }
Set-Location -LiteralPath $root

function Test-DotnetSdk([string] $path) {
    if (-not $path -or -not (Test-Path -LiteralPath $path)) { return $false }
    $sdks = & $path --list-sdks 2>$null
    return ($LASTEXITCODE -eq 0 -and $sdks)
}

$dotnet = $null
$candidates = @()
$onPath = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if ($onPath) { $candidates += $onPath }
$candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
foreach ($cand in $candidates) {
    if (Test-DotnetSdk $cand) { $dotnet = $cand; break }
}
if (-not $dotnet) { throw "nenhum dotnet com SDK encontrado. Instale o .NET SDK 10." }

$project = Join-Path $root 'tests\MagoDaPerformance.UiSnapshots\MagoDaPerformance.UiSnapshots.csproj'
& $dotnet run --project $project -c Release -- $OutputDir
if ($LASTEXITCODE -ne 0) { throw "geracao das capturas falhou (exit $LASTEXITCODE)." }

Write-Host ""
Get-ChildItem -LiteralPath $OutputDir -Filter *.png |
    ForEach-Object { Write-Host ("  {0}  ({1:0} KB)" -f $_.Name, ($_.Length / 1KB)) -ForegroundColor Green }
