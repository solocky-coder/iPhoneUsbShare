# iPhone USB Share — simple Windows EXE

This is a native C#/.NET 8 WPF implementation of the Windows-side orchestration used by
`0xbaksa/iphone-usb-reverse-tethering-windows`.

It is **not** a GUI wrapper around the PowerShell scripts. The app performs the PnP/registry,
libusb control-transfer, and Internet Connection Sharing operations itself. It uses the
libusb-win32 signed filter driver because Windows' composite USB stack does not expose the
iPhone's vendor control request directly.

## What the finished app does

1. Requests administrator elevation.
2. Detects an iPhone.
3. On first run, downloads libusb-win32 1.4.0.2 and verifies its SHA-256.
4. Installs the libusb-win32 filter scoped to the **currently connected Apple USB device PID** (Apple VID `05AC`); `12A8`/`12AB` are not used as the compatibility allowlist.
5. Enables CDC enumeration in `usbccgp` and detaches `AppleLowerFilter`.
6. Disables the PTP/photo-import interface while sharing to avoid iOS trust/reset races.
7. Performs the ordered mode sequence:
   - configuration index 2
   - restart
   - `GET_MODE` expecting `3:3:3:0`
   - configuration index 4
   - `SET_MODE 3`
8. Waits for Windows `UsbNcm` to expose a USB Ethernet adapter.
9. Enables Windows ICS from Wi-Fi → iPhone USB Ethernet.
10. Shows the phone's `192.168.137.x` address and live connection state.

## Build

Build target: **Windows 10 x64**. Development requires Visual Studio 2022 or the .NET 8 SDK.

```powershell
dotnet restore
dotnet publish .\src\iPhoneUsbShare.csproj -c Release -r win-x64 --self-contained true
```

The single-file executable is emitted under:

`src\bin\Release\net8.0-windows\win-x64\publish\iPhoneUsbShare.exe`

For a distributable build, copy that EXE to a clean Windows 10 x64 machine and test
with an iPhone or iPad using a data-capable cable.

## Important testing note

The upstream project reports testing on one iPhone / iOS / Windows combination. USB
descriptors and PnP behavior can differ by model/iOS version. The USB discovery and NCM-interface selection are now capability-based: the Apple PID is discovered dynamically, and CDC-NCM is selected from the Windows PnP `CDC_0D` interface collections created by `usbccgp`, rather than hard-coded `MI_02`/`MI_04` values. The Apple mode-switch sequence intentionally follows the proven Windows path: safe configuration 2 → restart → verify `GET_MODE` → arm configuration 4 → `SET_MODE(3)`.

If a model reports a different PID/configuration, the constants should be moved into a
device-profile table rather than blindly switching it.

## License / attribution

The integration logic is based on the public MIT-licensed project:

https://github.com/0xbaksa/iphone-usb-reverse-tethering-windows

The app also redistributes/obtains libusb-win32 under its upstream LGPL terms. Keep the
upstream license/notice with release builds.

## What is intentionally not included

No Apple software, private Apple components, or kernel patches are included. The app uses
Windows' built-in PnP/ICS functionality and the signed libusb-win32 filter driver.


## Getting the Windows EXE without building locally

This repository includes `.github/workflows/build-windows.yml`. On GitHub, open **Actions → Build Windows EXE → Run workflow**. GitHub's Windows runner compiles a self-contained x64 executable and uploads `iPhoneUsbShare-win-x64.zip` as an artifact. Extract it and run `iPhoneUsbShare.exe`; no .NET or Visual Studio installation is required on the target PC.

## iPad Air 2 support

The application does not define compatibility by Apple PID. It discovers the connected Apple VID `05AC` device and only proceeds when the device exposes the required mode-switch control path and Windows subsequently enumerates a CDC-NCM (`CDC_0D`) function. The iPad Air 2 observation remains useful as a test case, but it is no longer a special-case requirement in the code. Because iPadOS 15+ behavior across models has not been exhaustively tested, the application must verify the actual USB mode/control protocol instead of claiming universal compatibility from the PID alone.



### NCM / OS compatibility

This transport is only available on Apple OS versions/devices that actually expose the CDC-NCM mode. The referenced upstream research documents the two-function CDC-NCM behavior beginning with iOS 16, so **iOS/iPadOS 15 is not a compatibility claim for this implementation**; an iOS/iPadOS 15 device may switch modes but still never expose a `CDC_0D` NCM function. The app therefore fails at the capability-detection step instead of treating a PID as proof of compatibility.

Windows 10 supports USB NCM host operation on supported releases; Microsoft documents CDC/NCM support and the `UsbNcm.sys`/`UsbNcm.inf` driver path, while the CDC enumeration setting is required on Windows 10.

### Build package revision
The GitHub Actions package builds the self-contained Windows 10 x64 application and bundles only the libusb runtime needed for the user-space mode switch; the NCM network function is intended to use Windows’ in-box `UsbNcm` driver.

### Current build path
The old Apple Mobile Device Ethernet driver files remain in the repository for historical/reference purposes, but the current NCM path does not select them.

## Latest NCM implementation note

The Windows USB stack may expose Apple's NCM union as ordinary `VID_05AC&PID_xxxx&MI_nn` child nodes rather than a PnP ID containing `CDC_0D`, especially on Windows 10. The application therefore identifies NCM from the live USB descriptors, maps the descriptor interface number to the corresponding PnP child, and selects Microsoft's `UsbNcm` driver through SetupAPI. It does not hard-code `MI_02`, `MI_04`, or an Apple PID.

The first NCM control function is preferred because the iOS 16+ dual-function layout identifies the function with the interrupt endpoint as the tethering function; the second function is the RemoteXPC channel and is not expected to become a usable NIC.
