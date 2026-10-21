<#
.SYNOPSIS
  Removes the Lexi extension package. With -RemoveData also deletes %LOCALAPPDATA%\Lexi (dictionary database and raw downloads).
#>
param([switch]$RemoveData)

. "$PSScriptRoot\common.ps1"
if ($RemoveData) { Assert-RealAppData }
Stop-LexiExtensionProcess
$pkg = Get-AppxPackage -Name 'Lexi.SmartDictionary'
if ($pkg) {
    Remove-AppxPackage -Package $pkg.PackageFullName
    Write-Host "Removed $($pkg.PackageFullName) (its settings, history and caches in the package folder are deleted with it)."
} else {
    Write-Host "Lexi package is not installed."
}

$app = Join-Path $env:LOCALAPPDATA 'Lexi\app'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }

if ($RemoveData) {
    $root = Join-Path $env:LOCALAPPDATA 'Lexi'
    if (Test-Path $root) {
        Remove-Item $root -Recurse -Force
        Write-Host "Deleted $root (dictionary database and raw downloads)."
    }
} else {
    Write-Host "Dictionary data kept in $env:LOCALAPPDATA\Lexi (use -RemoveData to delete it)."
}
