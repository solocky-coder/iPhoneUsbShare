# Generates a driver catalog with Inf2Cat and signs it with SignTool.
# Windows SDK/WDK binaries remain installed under Windows Kits; they are not copied into this repo.
# The PFX contains a private signing key and MUST remain outside the repository.
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Inf,
    [Parameter(Mandatory)] [string]$PfxPath,
    [SecureString]$PfxPassword,
    [string]$OsVersions = '10_X64,10_X86,Server10_X64',
    [string]$TimestampUrl = 'http://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
function Find-Tool([string]$Name) {
  $roots = @((Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'),(Join-Path ${env:ProgramFiles} 'Windows Kits\10\bin')) | Where-Object { $_ -and (Test-Path $_) }
  foreach ($root in $roots) { $hit = Get-ChildItem $root -Filter $Name -File -Recurse -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1; if ($hit) { return $hit.FullName } }
  $cmd = Get-Command $Name -ErrorAction SilentlyContinue; if ($cmd) { return $cmd.Source }
  throw "$Name was not found. Install the Windows SDK/WDK or add its bin directory to PATH."
}
$infPath = (Resolve-Path $Inf).Path
$pfxResolved = (Resolve-Path $PfxPath).Path
$infDir = Split-Path -Parent $infPath
$catPath = Join-Path $infDir ([System.IO.Path]::GetFileNameWithoutExtension($infPath) + '.cat')
$inf2cat = Find-Tool 'Inf2Cat.exe'
$signtool = Find-Tool 'signtool.exe'
if (-not $PfxPassword) { $PfxPassword = Read-Host 'Enter the PFX password' -AsSecureString }
& $inf2cat "/driver:$infDir" "/os:$OsVersions"
if ($LASTEXITCODE -ne 0) { throw "Inf2Cat failed with exit code $LASTEXITCODE." }
if (-not (Test-Path $catPath)) { throw "Inf2Cat did not produce the expected catalog: $catPath" }
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($PfxPassword)
try {
  $plainPwd = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
  & $signtool sign /f $pfxResolved /p $plainPwd /fd SHA256 /tr $TimestampUrl /td SHA256 $catPath
  if ($LASTEXITCODE -ne 0) { throw "SignTool sign failed with exit code $LASTEXITCODE." }
} finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
& $signtool verify /pa /v $catPath
if ($LASTEXITCODE -ne 0) { Write-Warning 'SignTool verification failed on this machine; configure local trust before driver installation.' }
Write-Host "Signed catalog: $catPath"
