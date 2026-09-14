$ErrorActionPreference = 'Stop'
$RepoUrl = 'https://github.com/microsoft/NCM-Driver-for-Windows.git'
$Ref = 'release_2004'
$Root = Split-Path -Parent $PSScriptRoot
$Work = Join-Path $Root 'build\ncm-source'
$Patch = Join-Path $PSScriptRoot 'patches\apple-ncm-function-selection.patch'
$Out = Join-Path $Root 'artifacts\AppleNcm'

if (Test-Path $Work) { Remove-Item $Work -Recurse -Force }
New-Item -ItemType Directory -Force -Path $Out | Out-Null

# Never leave a stale driver package in the generated-driver path.  In
# particular, do not accidentally install a .sys/.inf from a previous build.
Get-ChildItem -Path $Out -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force

git clone --depth 1 --branch $Ref --recurse-submodules $RepoUrl $Work
Push-Location $Work
try {
    # Apply the Apple-specific function-selection change directly.  The upstream
    # release_2004 tree is old enough that a line-oriented git patch is brittle
    # against CRLF/patch-context handling on the hosted Windows runner.
    $deviceCpp = Join-Path $Work 'host\device.cpp'
    $deviceText = Get-Content $deviceCpp -Raw
    $startMarker = '    BYTE controlInterfaceNumber = 0;'
    $endMarker = '    BYTE numInterfaces = WdfUsbTargetDeviceGetNumInterfaces(m_WdfUsbTargetDevice);'
    $start = $deviceText.IndexOf($startMarker)
    $end = $deviceText.IndexOf($endMarker, $start)
    if ($start -lt 0 -or $end -lt 0) { throw 'Could not locate NCM descriptor-selection block in host/device.cpp.' }
    $replacement = @'
    BYTE controlInterfaceNumber = 0xff;
    BYTE dataInterfaceNumber = 0xff;
    BYTE unionDataInterfaceNumber = 0xff;
    BOOLEAN selectedNcmFunction = FALSE;
    PUSB_NCM_CS_FUNCTIONAL_DESCRIPTOR pNcmFunctionalDescr = nullptr;
    PUSB_ECM_CS_NET_FUNCTIONAL_DESCRIPTOR pEcmFunctionalDescr = nullptr;

    size_t currDescrptorOffset = 0;
    PUSB_COMMON_DESCRIPTOR pCurrDescriptor = (PUSB_COMMON_DESCRIPTOR) pDescriptors;

    while (currDescrptorOffset < pDescriptors->wTotalLength)
    {
        if (pCurrDescriptor->bLength == 0 ||
            currDescrptorOffset + pCurrDescriptor->bLength > pDescriptors->wTotalLength)
        {
            NCM_RETURN_NT_STATUS_IF_FALSE_MSG(FALSE,
                                              STATUS_DEVICE_HARDWARE_ERROR,
                                              "Bad USB descriptor length");
        }

        switch (pCurrDescriptor->bDescriptorType)
        {
            case USB_INTERFACE_DESCRIPTOR_TYPE:
            {
                NCM_RETURN_NT_STATUS_IF_FALSE_MSG(pCurrDescriptor->bLength == sizeof(USB_INTERFACE_DESCRIPTOR),
                                                  STATUS_DEVICE_HARDWARE_ERROR,
                                                  "Bad UsbInterfaceDescriptor");

                PUSB_INTERFACE_DESCRIPTOR pIfDescriptor = (PUSB_INTERFACE_DESCRIPTOR) pCurrDescriptor;

                if ((pIfDescriptor->bInterfaceClass == USB_CDC_INTERFACE_CLASS_COMM) &&
                    (pIfDescriptor->bInterfaceSubClass == USB_CDC_INTERFACE_SUBCLASS_NCM))
                {
                    // iOS 16+ exposes two NCM-like CDC functions.  The actual
                    // tethering function has one interrupt endpoint; the later
                    // RemoteXPC function has none. Select the first interrupt-
                    // backed control interface and never let the second one
                    // overwrite it.
                    if (!selectedNcmFunction && pIfDescriptor->bNumEndpoints == 1)
                    {
                        controlInterfaceNumber = pIfDescriptor->bInterfaceNumber;
                        selectedNcmFunction = TRUE;
                    }
                }
                else if (selectedNcmFunction &&
                         (pIfDescriptor->bInterfaceClass == USB_CDC_INTERFACE_CLASS_DATA) &&
                         (pIfDescriptor->bInterfaceProtocol == USB_DATA_INTERFACE_PROTOCOL_NCM) &&
                         (pIfDescriptor->bAlternateSetting == 1) &&
                         (pIfDescriptor->bNumEndpoints == 2) &&
                         (dataInterfaceNumber == 0xff))
                {
                    dataInterfaceNumber = pIfDescriptor->bInterfaceNumber;
                }

                break;
            }

            case USB_CS_INTERFACE_TYPE:
            {
                NCM_RETURN_NT_STATUS_IF_FALSE_MSG(pCurrDescriptor->bLength >= sizeof(USB_CDC_CS_FUNCTIONAL_DESCRIPTOR),
                                                  STATUS_DEVICE_HARDWARE_ERROR,
                                                  "Bad UsbCdcFunctionalDescriptor");

                PUSB_CDC_CS_FUNCTIONAL_DESCRIPTOR pCsFuncDescriptor =
                    (PUSB_CDC_CS_FUNCTIONAL_DESCRIPTOR) pCurrDescriptor;

                switch (pCsFuncDescriptor->bDescriptorSubtype)
                {
                    case USB_CS_INTF_SUBTYPE_CDC_UNION:
                    {
                        // The Union descriptor is the authoritative control ->
                        // data relationship. Only accept the union belonging
                        // to the control interface we selected above.
                        if (selectedNcmFunction && pCurrDescriptor->bLength >= 5)
                        {
                            const PUCHAR bytes = (PUCHAR)pCurrDescriptor;
                            if (bytes[3] == controlInterfaceNumber)
                            {
                                unionDataInterfaceNumber = bytes[4];
                            }
                        }
                        break;
                    }

                    case USB_CS_NCM_FUNCTIONAL_DESCR_TYPE:
                    {
                        if (selectedNcmFunction && pNcmFunctionalDescr == nullptr)
                        {
                            NCM_RETURN_NT_STATUS_IF_FALSE_MSG(
                                pCsFuncDescriptor->bFunctionLength == sizeof(USB_NCM_CS_FUNCTIONAL_DESCRIPTOR),
                                STATUS_DEVICE_HARDWARE_ERROR,
                                "Bad UsbNcmFunctionalDescriptor");

                            pNcmFunctionalDescr = (PUSB_NCM_CS_FUNCTIONAL_DESCRIPTOR) pCsFuncDescriptor;
                        }
                        break;
                    }

                    case USB_CS_ECM_FUNCTIONAL_DESCR_TYPE:
                    {
                        if (selectedNcmFunction && pEcmFunctionalDescr == nullptr)
                        {
                            NCM_RETURN_NT_STATUS_IF_FALSE_MSG(
                                pCsFuncDescriptor->bFunctionLength == sizeof(USB_ECM_CS_NET_FUNCTIONAL_DESCRIPTOR),
                                STATUS_DEVICE_HARDWARE_ERROR,
                                "Bad UsbEcmFunctionalDescriptor");

                            pEcmFunctionalDescr = (PUSB_ECM_CS_NET_FUNCTIONAL_DESCRIPTOR) pCsFuncDescriptor;
                        }
                        break;
                    }
                }

                break;
            }
        }

        currDescrptorOffset += pCurrDescriptor->bLength;
        pCurrDescriptor = (PUSB_COMMON_DESCRIPTOR)(((PUINT8)pCurrDescriptor) + pCurrDescriptor->bLength);
    }

    if (unionDataInterfaceNumber != 0xff)
    {
        dataInterfaceNumber = unionDataInterfaceNumber;
    }

    NCM_RETURN_NT_STATUS_IF_FALSE_MSG(selectedNcmFunction &&
                                      controlInterfaceNumber != 0xff &&
                                      dataInterfaceNumber != 0xff,
                                      STATUS_DEVICE_HARDWARE_ERROR,
                                      "Apple NCM function was not fully identified");

    KdPrint(("AppleNcm: descriptor selection control=%u data=%u unionData=%u\n",
             controlInterfaceNumber,
             dataInterfaceNumber,
             unionDataInterfaceNumber));

'@
    $deviceText = $deviceText.Substring(0, $start) + $replacement + $deviceText.Substring($end)
    Set-Content -Path $deviceCpp -Value $deviceText -Encoding UTF8

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
    $text = $text -replace '(?m)^\s*%AppleNcm.DeviceDesc%=AppleNcm_Device,USB\\MS_COMP_WINNCM\s*$', '%AppleNcm.DeviceDesc%=AppleNcm_Device,USB\VID_05AC&PID_12AB&MI_02'
    $text = $text -replace '(?m)^\s*;.*USB\\Class_02&SubClass_0d&Prot_00.*$', ''
    $text = $text -replace 'CatalogFile=UsbNcmFnSample\.cat', 'CatalogFile=AppleNcm.cat'
    $text = $text -replace 'UsbNcm.DeviceDesc', 'AppleNcm.DeviceDesc'
    $text = $text -replace 'UsbNcm Host Device', 'Apple iPhone NCM Host Device'
    Set-Content -Path $inf -Value $text -Encoding Unicode

    $sln = Join-Path $Work 'usbncm.sln'
    if (-not (Get-Command msbuild.exe -ErrorAction SilentlyContinue)) {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (Test-Path $vswhere) {
            $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
            if ($vs) { & "$vs\Common7\Tools\Launch-VsDevShell.ps1" -Arch amd64 }
        }
    }
    msbuild (Join-Path $Work 'usbncm.sln') /m /p:Configuration=Release /p:Platform=x64 /p:TargetName=AppleNcm /p:DisableSpecificWarnings=4996

    $sys = Get-ChildItem -Path $Work -Filter '*.sys' -Recurse | Where-Object { $_.Name -match 'UsbNcmSample|AppleNcm' } | Select-Object -First 1
    if (-not $sys) { throw 'AppleNcm.sys was not produced.' }
    Copy-Item $sys.FullName (Join-Path $Out 'AppleNcm.sys') -Force
    Copy-Item $inf (Join-Path $Out 'AppleNcm.inf') -Force

    # The upstream INF names its catalog explicitly.  After the INF is renamed
    # to AppleNcm.inf we also rename that CatalogFile entry above, so the build
    # must produce AppleNcm.cat.  Fail instead of emitting an apparently usable
    # package with a missing catalog.
    $cat = Get-ChildItem -Path $Work -Filter 'AppleNcm.cat' -Recurse | Select-Object -First 1
    if (-not $cat) { throw 'AppleNcm.cat was not produced. The generated package would not be installable as a signed driver package.' }
    Copy-Item $cat.FullName (Join-Path $Out 'AppleNcm.cat') -Force

    # Validate the generated package contents before publishing the artifact.
    $outInf = Join-Path $Out 'AppleNcm.inf'
    $outText = Get-Content $outInf -Raw
    foreach ($required in @('USB\VID_05AC&PID_12AB&MI_02','Service,    0, "AppleNcm"','ServiceBinary  = %12%\AppleNcm.sys','CatalogFile=AppleNcm.cat')) {
        if ($outText -notmatch [regex]::Escape($required)) {
            throw "Generated AppleNcm.inf is missing required package content: $required"
        }
    }
    if (-not (Test-Path (Join-Path $Out 'AppleNcm.sys'))) { throw 'Generated package is missing AppleNcm.sys.' }
    if (-not (Test-Path (Join-Path $Out 'AppleNcm.cat'))) { throw 'Generated package is missing AppleNcm.cat.' }

    Copy-Item (Join-Path $PSScriptRoot 'patches\README.md') (Join-Path $Out 'source-modification-notes.md') -Force
}
finally { Pop-Location }
Write-Host "Built package: $Out"
