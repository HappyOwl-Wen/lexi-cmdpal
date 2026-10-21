<#
.SYNOPSIS
  Runs the extension's built-in self-test (Lexi.Extension.exe --self-test): the real provider, pages, details,
  fallback and settings form are driven in-process against the installed dictionary, without Command Palette.
.PARAMETER Package
  Test the shipped bits: extracts the newest .msix in artifacts\msix (or -MsixPath) to a temporary folder.
.PARAMETER Installed
  Test the files of the registered Lexi package.
.PARAMETER Ai
  Also run one real Claude Code request (uses your Claude Code quota).
.PARAMETER Network
  Also download pronunciation recordings from Wikimedia Commons (house, weakness, Haus; not played) and check the British-first messages.
.PARAMETER Translation
  Also translate two sample sentences through Google (no Claude or API key).
#>
param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Debug',
    [switch]$Package,
    [string]$MsixPath,
    [switch]$Installed,
    [switch]$Ai,
    [switch]$Network,
    [switch]$Translation
)

. "$PSScriptRoot\common.ps1"
$extracted = $null
if ($Installed) {
    $pkg = Get-AppxPackage -Name 'Lexi.SmartDictionary'
    if (-not $pkg) { throw 'Lexi is not installed (scripts\install.ps1).' }
    $exe = Join-Path $pkg.InstallLocation 'Lexi.Extension.exe'
} elseif ($Package -or $MsixPath) {
    if (-not $MsixPath) {
        $found = Get-ChildItem (Join-Path $RepoRoot 'artifacts\msix') -Recurse -Filter '*.msix' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $found) { throw 'No package in artifacts\msix (run scripts\package.ps1 first).' }
        $MsixPath = $found.FullName
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $extracted = Join-Path ([IO.Path]::GetTempPath()) ('lexi-msix-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    [IO.Compression.ZipFile]::ExtractToDirectory($MsixPath, $extracted)
    $exe = Join-Path $extracted 'Lexi.Extension.exe'
} else {
    $exe = Join-Path (Get-LexiExtensionOutputDir $Configuration) 'Lexi.Extension.exe'
}
if (-not (Test-Path $exe)) { throw "Not found: $exe (build first)." }

$report = Join-Path ([IO.Path]::GetTempPath()) 'lexi-selftest.txt'
$arguments = @('--self-test', "`"$report`"")
if ($Ai) { $arguments += '--ai' }
if ($Network) { $arguments += '--network' }
if ($Translation) { $arguments += '--translation' }

Write-Host "Running $exe --self-test"
$code = 1
try {
    $p = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru -WindowStyle Hidden
    $code = $p.ExitCode
    Get-Content -LiteralPath $report -Encoding UTF8 | ForEach-Object { Write-Host $_ }
    Write-Host "Report: $report (exit code $code)"
} finally {
    if ($extracted) { Remove-Item $extracted -Recurse -Force -ErrorAction SilentlyContinue }
}
exit $code
