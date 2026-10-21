<#
.SYNOPSIS
  Builds the Release MSIX (self-contained, trimmed, ReadyToRun) into artifacts\msix. Optionally signs it.
.PARAMETER Sign
  Sign with a self-signed code-signing certificate whose subject equals the manifest Publisher (CN=LexiDev).
  The certificate is created in the current user's personal store if missing and exported to artifacts\LexiDev.cer.
  Installing the signed package on a PC requires trusting that .cer in LocalMachine\TrustedPeople (administrator).
.PARAMETER PassThru
  Write the path of the produced .msix to the pipeline.
#>
param(
    [ValidateSet('x64', 'ARM64')] [string]$Platform = 'x64',
    [switch]$Sign,
    [switch]$SkipBuild,
    [switch]$PassThru
)

. "$PSScriptRoot\common.ps1"
$out = Join-Path $RepoRoot 'artifacts\msix'
$rid = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }

if (-not $SkipBuild) {
    $dotnet = Get-LexiDotnet
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    & $dotnet publish (Join-Path $RepoRoot 'src\Lexi.Extension\Lexi.Extension.csproj') -c Release --nologo `
        "-p:Platform=$Platform" "-p:RuntimeIdentifier=$rid" "-p:PublishProfile=$rid" `
        -p:GenerateAppxPackageOnBuild=true "-p:AppxPackageDir=$out\" -p:AppxBundle=Never `
        -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxPackageSigningEnabled=false | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publish failed ($LASTEXITCODE)." }
}

$msix = Get-ChildItem $out -Recurse -Filter '*.msix' | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw "No .msix produced in $out" }

if ($Sign) {
    $subject = 'CN=LexiDev'
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.HasPrivateKey } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature -FriendlyName 'Lexi local signing' `
            -CertStoreLocation Cert:\CurrentUser\My -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    }
    $cer = Join-Path $RepoRoot 'artifacts\LexiDev.cer'
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    $signtool = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') -Recurse -Filter signtool.exe |
        Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
    if (-not $signtool) { throw "signtool.exe not found (restore the Microsoft.Windows.SDK.BuildTools package by building once)." }
    & $signtool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint $msix.FullName | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Signing failed." }
    Write-Host "Signed $($msix.FullName); certificate: $cer"
}

Write-Host "Package: $($msix.FullName) ($([math]::Round($msix.Length / 1MB, 1)) MB)"
if ($PassThru) { $msix.FullName }
