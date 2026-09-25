# Creates a self-signed code-signing certificate for local/test driver signing.
# The public .cer may be committed to the repository.
# The private .pfx MUST remain outside the repository.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Subject,
    [Parameter(Mandatory)] [string]$OutDir,
    [SecureString]$PfxPassword,
    [int]$ValidityYears = 10,
    [switch]$ReuseExisting
)
$ErrorActionPreference = 'Stop'
if (-not $PfxPassword) { $PfxPassword = Read-Host 'Enter a password for the private PFX' -AsSecureString }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } | Sort-Object NotAfter -Descending | Select-Object -First 1
if ($ReuseExisting -and $existing) { $cert = $existing } else {
  $cert = New-SelfSignedCertificate -Subject $Subject -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -KeyUsage DigitalSignature -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3') -NotAfter (Get-Date).AddYears($ValidityYears)
}
$cerPath = Join-Path $OutDir 'WinUsbControlSigning.cer'
$pfxPath = Join-Path $OutDir 'WinUsbControlSigning.pfx'
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $PfxPassword | Out-Null
Write-Host "Public certificate: $cerPath"
Write-Host "Private PFX: $pfxPath"
Write-Warning 'Do NOT commit the PFX/private key to source control.'