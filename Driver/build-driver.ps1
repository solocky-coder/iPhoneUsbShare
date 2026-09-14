$ErrorActionPreference = 'Stop'
$RepoUrl = 'https://github.com/microsoft/NCM-Driver-for-Windows.git'
$Ref = 'release_2004'
$Root = Split-Path -Parent $PSScriptRoot
$Work = Join-Path $Root 'build\ncm-source'
$Patch = Join-Path $PSScriptRoot 'patches\apple-ncm-function-selection.patch'
$Out = Join-Path $Root 'artifacts\AppleNcm'

if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

git clone --depth 1 --branch $Ref --recurse-submodules $RepoUrl $Work
Push-Location $Work
try {
    git apply --check --whitespace=nowarn $Patch
    git apply --whitespace=nowarn $Patch

    $inf = Join-Path $Work 'host\UsbNcmSample.inf'
    $text = Get-Content $inf -Raw
    $text = $text -replace 'UsbNcmSample', 'AppleNcm'
    $text = $text -replace '"UsbNcm"', '"AppleNcm"'
    $text = $text -replace 'UsbNcm_Device', 'AppleNcm_Device'
    $text = $text -replace 'UsbNcm_Service_Inst', 'AppleNcm_Service_Inst'
    $text = $text -replace 'UsbNcm_Service', 'AppleNcm_Service'
    $text = $text -replace 'UsbNcm_wdfsect', 'AppleNcm_wdfsect'
    $text = $text -replace 'UsbNcm.DeviceDesc', 'AppleNcm.DeviceDesc'
    $text = $text -replace 'UsbNcm.SVCDESC', 'AppleNcm.SVCDESC'
    $text = $text -replace '(?m)^\s*%UsbNcm.DeviceDesc%=UsbNcm_Device,USB\\MS_COMP_WINNCM\s*$', '%AppleNcm.DeviceDesc%=AppleNcm_Device,USB\VID_05AC&PID_12AB&MI_02'
    $text = $text -replace '(?m)^\s*;.*USB\\Class_02&SubClass_0d&Prot_00.*$', ''
    $text = $text -replace 'UsbNcm.DeviceDesc', 'AppleNcm.DeviceDesc'
    $text = $text -replace 'UsbNcm Host Device', 'Apple iPhone NCM Host Device'
    Set-Content -Path $inf -Value $text -Encoding Unicode

    $msbuild = $null
    $cmd = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($cmd) {
        $msbuild = $cmd.Source
    }

    if (-not $msbuild) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $vswhere) {
            $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            if ($vs) {
                $candidate = Join-Path $vs 'MSBuild\Current\Bin\MSBuild.exe'
                if (Test-Path $candidate) { $msbuild = $candidate }
            }
        }
    }

    if (-not $msbuild) {
        throw 'MSBuild.exe was not found on the Windows runner.'
    }

    & $msbuild (Join-Path $Work 'projects\host\host.vcxproj') /m /p:Configuration=Release /p:Platform=x64 /p:TargetName=AppleNcm
    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE." }

    $sys = Get-ChildItem -Path $Work -Filter '*.sys' -Recurse | Where-Object { $_.Name -match 'UsbNcmSample|AppleNcm' } | Select-Object -First 1
    if (-not $sys) { throw 'AppleNcm.sys was not produced.' }
    Copy-Item $sys.FullName (Join-Path $Out 'AppleNcm.sys') -Force
    Copy-Item $inf (Join-Path $Out 'AppleNcm.inf') -Force
    if (Test-Path (Join-Path $Work 'host\AppleNcm.cat')) { Copy-Item (Join-Path $Work 'host\AppleNcm.cat') (Join-Path $Out 'AppleNcm.cat') -Force }
    Copy-Item $Patch (Join-Path $Out 'apple-ncm-function-selection.patch') -Force
}
finally { Pop-Location }
Write-Host "Built package: $Out"
