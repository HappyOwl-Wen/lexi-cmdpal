<#
.SYNOPSIS
  Downloads the raw dictionary sources and builds %LOCALAPPDATA%\Lexi\data\lexi.db (repeatable).
.DESCRIPTION
  1. Builds the Lexi CLI (Release).
  2. `lexi download`: fetches ECDICT, FrequencyWords and the Kaikki/wiktextract exports (~1.2 GB, skipped if present).
  3. `lexi import`: builds the SQLite dictionary (~500 MB; measured 9–10 minutes on a Ryzen 9 7940H laptop).
  4. `lexi quality`: runs the acceptance list against the new database.
  The raw files are kept in %LOCALAPPDATA%\Lexi\raw so re-imports do not download again (use -Force to refresh).
.PARAMETER NoEcdict
  Build a "redistributable" database from CC BY-SA sources only (ECDICT content licensing is not confirmed).
.PARAMETER Limit
  Import at most N entries per source (quick test build).
#>
param(
    [switch]$NoEcdict,
    [switch]$Force,
    [int]$Limit = 0,
    [string]$RawDir,
    [string]$Out
)

. "$PSScriptRoot\common.ps1"
if (-not $Out -and -not $RawDir) { Assert-RealAppData }
$dotnet = Get-LexiDotnet

& $dotnet build (Join-Path $RepoRoot 'src\Lexi.Cli\Lexi.Cli.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "CLI build failed." }
$lexi = Join-Path $RepoRoot 'src\Lexi.Cli\bin\Release\net10.0\lexi.dll'

$common = @()
if ($RawDir) { $common += @('--raw', $RawDir) }

$download = @('download') + $common
if ($NoEcdict) { $download += '--no-ecdict' }
if ($Force) { $download += '--force' }
& $dotnet $lexi @download
if ($LASTEXITCODE -ne 0) { throw "Download failed." }

# The extension keeps the database open while Command Palette runs; stop it so the file can be replaced.
Stop-LexiExtensionProcess

$import = @('import') + $common
if ($NoEcdict) { $import += '--no-ecdict' }
if ($Limit -gt 0) { $import += @('--limit', $Limit) }
if ($Out) { $import += @('--out', $Out) }
& $dotnet $lexi @import
if ($LASTEXITCODE -ne 0) { throw "Import failed." }

$quality = @('quality')
if ($Out) { $quality += @('--db', $Out) }
& $dotnet $lexi @quality
