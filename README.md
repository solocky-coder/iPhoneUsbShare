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
descriptors and PnP behavior can differ by model/iOS version. The USB discovery and NCM-interface selection are now capability-based: the Apple PID is discovered dynamically, and CDC-NCM control interfaces are selected from USB descriptors rather than hard-coded `MI_02`/`MI_04` values. The current Apple mode-switch protocol still assumes configuration values 2/4 and `SET_MODE 3`; those remain the next compatibility layer to make fully descriptor/profile driven.

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

The application recognizes Apple USB PID `12AB` (in addition to iPhone PID `12A8`) and uses the same CDC-NCM control path only after the Apple USB device has been detected. The iPad Air 2 observation remains useful as a test case, but it is no longer a special-case requirement in the code. Because iPadOS 15+ behavior across models has not been exhaustively tested, the application must verify the actual USB mode/control protocol instead of claiming universal compatibility from the PID alone.


### Build package revision
The GitHub Actions package includes the compile fixes for USB adapter detection, required .NET namespaces, and the bundled Apple Ethernet driver path for Windows 10 testing.

### Current build path
The workflow now packages the Apple Mobile Device Ethernet driver files from `NetDrivers` for the Windows 10 iPad Air 2 test path.
