<#
.SYNOPSIS
  Builds the Lexi extension and registers the build output as a loose MSIX layout
  (the command-line equivalent of Visual Studio "Deploy").
.DESCRIPTION
  Requires Windows "Developer Mode" (Settings > System > For developers), which allows
  registering unsigned package layouts. No administrator rights are needed otherwise.
  Command Palette picks up the (re)registered package automatically; if it does not,
  run "Reload" inside Command Palette.
#>
param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Debug',
    [ValidateSet('x64', 'ARM64')] [string]$Platform = 'x64',
    [switch]$SkipBuild
)

. "$PSScriptRoot\common.ps1"

if (-not (Test-DeveloperMode)) {
    throw "Windows Developer Mode is off. Enable it in Settings > System > For developers > Developer Mode, then run this script again."
}

Stop-LexiExtensionProcess

if (-not $SkipBuild) {
    $dotnet = Get-LexiDotnet
    & $dotnet build (Join-Path $RepoRoot 'src\Lexi.Extension\Lexi.Extension.csproj') -c $Configuration -p:Platform=$Platform
    if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)." }
}

$layout = Get-LexiExtensionOutputDir $Configuration $Platform
$manifest = Join-Path $layout 'AppxManifest.xml'
if (-not (Test-Path $manifest)) { throw "Layout manifest not found: $manifest" }

# A package registered from another folder (e.g. by install.ps1) must be removed first.
$existing = Get-AppxPackage -Name 'Lexi.SmartDictionary'
if ($existing -and $existing.InstallLocation -ne $layout) {
    Write-Host "Removing the registration from $($existing.InstallLocation) (extension settings/history are reset)."
    Remove-AppxPackage -Package $existing.PackageFullName
}

Write-Host "Registering $manifest"
Add-AppxPackage -Register $manifest -ForceUpdateFromAnyVersion -ForceApplicationShutdown
$pkg = Get-AppxPackage -Name 'Lexi.SmartDictionary'
Write-Host ("Registered {0} {1} from {2}" -f $pkg.Name, $pkg.Version, $pkg.InstallLocation)
Write-Host "Open Command Palette (your hotkey), type 'Lexi' or 'dict'. If it does not show up, run 'Reload' in Command Palette."
