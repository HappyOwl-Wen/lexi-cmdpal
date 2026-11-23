<#
.SYNOPSIS
  Adds the Windows British English text-to-speech voice used by Lexi.
.DESCRIPTION
  Run in an elevated Windows PowerShell. Downloads the en-GB TTS capability
  and its dependencies through Windows Update. Does not change the display
  language or default voice. Already installed components are skipped.
#>
[CmdletBinding()]
param([string]$LogPath)

$ErrorActionPreference = 'Stop'
$logging = $false
try {
    if ($LogPath) {
        Start-Transcript -LiteralPath $LogPath -Force | Out-Null
        $logging = $true
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script in Windows PowerShell as administrator.'
    }

    $name = 'Language.TextToSpeech~~~en-GB~0.0.1.0'
    $capability = Get-WindowsCapability -Online -Name $name
    if (-not $capability) { throw "Windows did not report capability $name" }
    if ($capability.State -ne 'Installed') {
        Write-Host "Installing $name through Windows Update..."
        $result = Add-WindowsCapability -Online -Name $name
        if ($result.RestartNeeded) {
            Write-Host 'Windows requires a restart to finish installing the British voice.'
            exit 3010
        }
    }
    $capability = Get-WindowsCapability -Online -Name $name
    if ($capability.State -ne 'Installed') {
        throw "British TTS is not installed: $($capability.State)"
    }

    $voices = [Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media.SpeechSynthesis, ContentType=WindowsRuntime]::AllVoices
    $british = @($voices | Where-Object Language -eq 'en-GB')
    if ($british.Count -eq 0) {
        throw 'TTS component installed, but Windows has not exposed en-GB voices yet. Restart Windows and run this script again.'
    }
    $british | Select-Object DisplayName, Language | Format-Table | Out-Host
    Write-Host 'British voice is available. Restart Command Palette to refresh its installed voices.'
} catch {
    Write-Host "British voice installation failed: $_"
    exit 1
} finally {
    if ($logging) { Stop-Transcript | Out-Null }
}
