# Shared helpers for Lexi scripts. Dot-source: . "$PSScriptRoot\common.ps1"

$ErrorActionPreference = 'Stop'
$script:RepoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Get-LexiDotnet {
    <# Returns the path of a dotnet executable that has a .NET 10 SDK (PATH first, then the per-user install). #>
    $candidates = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
    foreach ($c in $candidates | Select-Object -Unique) {
        if (-not (Test-Path $c)) { continue }
        $sdks = & $c --list-sdks 2>$null
        if ($sdks | Where-Object { $_ -match '^10\.' }) { return $c }
    }
    throw @"
No .NET 10 SDK found. Install it without admin rights:
  Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
  powershell -ExecutionPolicy Bypass -File dotnet-install.ps1 -Channel 10.0 -InstallDir "$env:LOCALAPPDATA\Microsoft\dotnet"
"@
}

function Test-DeveloperMode {
    $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
    try {
        $v = Get-ItemProperty -Path $key -Name AllowDevelopmentWithoutDevLicense -ErrorAction Stop
        return ($v.AllowDevelopmentWithoutDevLicense -eq 1)
    } catch {
        return $false
    }
}

function Get-AppDataRedirection {
    <# Returns the package folder name when this shell's writes to %LOCALAPPDATA% are redirected into a packaged
       app's private LocalCache (a terminal started by an MSIX app such as the Claude desktop app), else $null. #>
    $name = 'lexi-probe-' + [guid]::NewGuid().ToString('N') + '.tmp'
    $probe = Join-Path $env:LOCALAPPDATA $name
    try {
        Set-Content -LiteralPath $probe -Value '' -Encoding Ascii
        $hit = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "LocalCache\Local\$name") } |
            Select-Object -First 1
        if ($hit) { return $hit.Name }
        return $null
    } finally {
        Remove-Item -LiteralPath $probe -ErrorAction SilentlyContinue
    }
}

function Assert-RealAppData {
    $package = Get-AppDataRedirection
    if ($package) {
        throw @"
This shell runs inside the packaged app '$package'. Windows redirects its writes to %LOCALAPPDATA% into
$env:LOCALAPPDATA\Packages\$package\LocalCache\Local, where Command Palette cannot see them.
Run this script from a normal terminal (PowerShell from the Start menu, or Windows Terminal).
"@
    }
}

function Move-LexiTree([string]$From, [string]$To) {
    New-Item -ItemType Directory -Force $To | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $From -Force) {
        $target = Join-Path $To $item.Name
        if ($item.PSIsContainer -and (Test-Path -LiteralPath $target)) {
            Move-LexiTree $item.FullName $target
        } else {
            Move-Item -LiteralPath $item.FullName -Destination $target -Force
        }
    }
}

function Move-LexiRedirectedData {
    <# Adopts Lexi data that an earlier run wrote into a packaged app's LocalCache (see Get-AppDataRedirection). #>
    $real = Join-Path $env:LOCALAPPDATA 'Lexi'
    if (Test-Path (Join-Path $real 'data\lexi.db')) { return }
    $source = Get-ChildItem (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -ErrorAction SilentlyContinue |
        ForEach-Object { Join-Path $_.FullName 'LocalCache\Local\Lexi' } |
        Where-Object { Test-Path (Join-Path $_ 'data\lexi.db') } |
        Select-Object -First 1
    if (-not $source) { return }
    Write-Host "Found Lexi data in another app's private folder: $source"
    Write-Host "Moving it to $real ..."
    Move-LexiTree $source $real
    Remove-Item -LiteralPath $source -Recurse -Force -ErrorAction SilentlyContinue
}

function Stop-LexiExtensionProcess {
    $p = Get-Process -Name 'Lexi.Extension' -ErrorAction SilentlyContinue
    if ($p) {
        Write-Host "Stopping running Lexi.Extension process ($($p.Id -join ', '))..."
        $p | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }
}

function Get-LexiExtensionOutputDir([string]$Configuration = 'Debug', [string]$Platform = 'x64') {
    $rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
    return Join-Path $script:RepoRoot "src\Lexi.Extension\bin\$Platform\$Configuration\net10.0-windows10.0.26100.0\$rid"
}
