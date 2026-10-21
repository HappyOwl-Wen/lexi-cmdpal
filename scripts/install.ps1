<#
.SYNOPSIS
  Installs the optimized Release build of Lexi for the current user (no admin rights, no certificate).
.DESCRIPTION
  1. Publishes the extension as an MSIX (self-contained .NET, trimmed, ReadyToRun).
  2. Unpacks it to %LOCALAPPDATA%\Lexi\app\<version> (a stable location, independent of the source tree).
  3. Registers that folder as a package — this requires Windows "Developer Mode".
  Command Palette picks the extension up automatically (or run "Reload" in Command Palette).
  The dictionary database (%LOCALAPPDATA%\Lexi\data\lexi.db) is built separately: scripts\import-data.ps1.
.PARAMETER Database
  A dictionary database built elsewhere (e.g. artifacts\data\lexi.db from `lexi import --out`) to install as
  %LOCALAPPDATA%\Lexi\data\lexi.db, replacing the current one. The extension is stopped first (it keeps the file open).
.NOTES
  To install on another PC without Developer Mode, sign the package with scripts\package.ps1 -Sign and trust
  the certificate there (that step needs administrator rights).
#>
param(
    [ValidateSet('x64', 'ARM64')] [string]$Platform = 'x64',
    [switch]$SkipBuild,
    [string]$Database
)

. "$PSScriptRoot\common.ps1"
Add-Type -AssemblyName System.IO.Compression.FileSystem

Assert-RealAppData
if (-not (Test-DeveloperMode)) {
    throw "Windows Developer Mode is off. Enable it in Settings > System > For developers > Developer Mode, then run this script again."
}

Move-LexiRedirectedData
$databaseSource = $null
if ($Database) {
    # Relative paths may be given from the repository root, whatever the current directory is.
    $candidates = @($Database)
    if (-not [IO.Path]::IsPathRooted($Database)) { $candidates += Join-Path $RepoRoot $Database }
    $databaseSource = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (-not $databaseSource) { throw "Database file not found: $($candidates -join ' or ')" }
    $databaseSource = (Resolve-Path -LiteralPath $databaseSource).Path
    Write-Host "Will install dictionary database $databaseSource"
}

$msix = & "$PSScriptRoot\package.ps1" -Platform $Platform -SkipBuild:$SkipBuild -PassThru
$zip = [IO.Compression.ZipFile]::OpenRead($msix)
try {
    $entry = $zip.GetEntry('AppxManifest.xml')
    $reader = New-Object IO.StreamReader($entry.Open())
    [xml]$manifest = $reader.ReadToEnd()
    $reader.Dispose()
} finally {
    $zip.Dispose()
}
$version = $manifest.Package.Identity.Version
$name = $manifest.Package.Identity.Name

$appRoot = Join-Path $env:LOCALAPPDATA 'Lexi\app'
$target = Join-Path $appRoot "$version-$Platform"

Stop-LexiExtensionProcess
if ($databaseSource) {
    # Copy next to the target first, so an interrupted copy never leaves a truncated lexi.db behind.
    $dataDir = Join-Path $env:LOCALAPPDATA 'Lexi\data'
    New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
    $databaseTarget = Join-Path $dataDir 'lexi.db'
    Copy-Item -LiteralPath $databaseSource -Destination "$databaseTarget.new" -Force
    Move-Item -LiteralPath "$databaseTarget.new" -Destination $databaseTarget -Force
    Write-Host ("Dictionary database installed: {0} ({1:N0} MB)" -f $databaseTarget, ((Get-Item -LiteralPath $databaseTarget).Length / 1MB))
}

$existing = Get-AppxPackage -Name $name
if ($existing -and $existing.InstallLocation -ne $target) {
    Write-Host "Removing previous registration from $($existing.InstallLocation) (extension settings/history are reset)."
    Remove-AppxPackage -Package $existing.PackageFullName
}

if (Test-Path $target) { Remove-Item $target -Recurse -Force }
[IO.Compression.ZipFile]::ExtractToDirectory($msix, $target)
# Packaging artifacts that are not part of a loose layout.
foreach ($f in 'AppxBlockMap.xml', 'AppxSignature.p7x', '[Content_Types].xml') {
    $p = Join-Path $target $f
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force }
}
if (Test-Path (Join-Path $target 'AppxMetadata')) { Remove-Item (Join-Path $target 'AppxMetadata') -Recurse -Force }

Add-AppxPackage -Register (Join-Path $target 'AppxManifest.xml') -ForceUpdateFromAnyVersion -ForceApplicationShutdown
$pkg = Get-AppxPackage -Name $name
Write-Host ("Installed {0} {1} from {2}" -f $pkg.Name, $pkg.Version, $pkg.InstallLocation)

# Keep only the installed version.
Get-ChildItem $appRoot -Directory | Where-Object { $_.FullName -ne $target } | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

if (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'Lexi\data\lexi.db'))) {
    Write-Warning "No dictionary database yet. Build it with: powershell -ExecutionPolicy Bypass -File scripts\import-data.ps1"
}
Write-Host "Open Command Palette, type 'Lexi' or 'dict'. If it does not appear, run 'Reload' in Command Palette."
