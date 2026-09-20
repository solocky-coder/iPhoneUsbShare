# iPhone USB Share — simple Windows EXE

This is a native C#/.NET 8 WPF implementation of the Windows-side orchestration used by
`0xbaksa/iphone-usb-reverse-tethering-windows`.

It is **not** a GUI wrapper around the PowerShell scripts. The app performs the PnP/registry,
WinUSB control-transfer, and Internet Connection Sharing operations itself.

## What the finished app does

1. Requests administrator elevation.
2. Detects an iPhone/iPad.
3. Removes the legacy libusb-win32 upper filter from the Apple composite devnode when migrating an existing installation.
4. Binds the Microsoft in-box WinUSB function driver to the Apple MI_00 control interface and registers an application device-interface GUID.
5. Enables CDC enumeration in `usbccgp` and detaches `AppleLowerFilter`.
6. Disables the PTP/photo-import interface when it is still exposed as WPD.
7. Performs the ordered Apple mode sequence:
   - configuration index 2
   - restart
   - `GET_MODE` expecting `3:3:3:0` or the accepted three-byte form
   - configuration index 4
   - `SET_MODE 3`
8. Reads live USB descriptors through WinUSB and identifies the tethering CDC-NCM control interface by its interrupt-IN notification endpoint.
9. Rejects the second CDC-NCM-like RemoteXPC function when it has no interrupt notification endpoint.
10. Selects Microsoft's `UsbNcm` driver on the descriptor-identified NCM child.
11. Applies the selected **mode** (below) to the USB Ethernet adapter: either an isolated Direct USB link, or Windows ICS reverse tethering from this PC's Wi-Fi → iPhone/iPad USB Ethernet.

## Modes

Steps 1–10 (WinUSB control path, Apple mode switch, descriptor-based NCM selection, Microsoft `UsbNcm` bind) are identical in both modes. The mode only decides how the resulting USB Ethernet adapter is configured. Pick it in the **Mode** card of the main window (the choice is remembered in `%ProgramData%\iPhoneUsbShare\mode.txt`). It can be switched at any time, including while a device is connected and sharing: the network side (DHCP server or ICS) is torn down and restarted in the new mode without redoing the USB bring-up.

| Mode | What it does | Devices |
| --- | --- | --- |
| **Direct USB** (default) | Isolated point-to-point link: static IPv4 on the PC (`192.168.99.1`, `.100.1`, `.101.1`, `.102.1`), built-in DHCP server leasing the device a fixed peer address. No gateway, no DNS, no ICS/NAT. Windows sharing state is never modified. | up to 4 |
| **Reverse tethering** | Windows Internet Connection Sharing from this PC's uplink (Wi-Fi preferred, otherwise the adapter that has a default gateway) to the device's USB Ethernet adapter (`192.168.137.x`). The device uses this PC's internet. ICS is disabled again on Stop or when the device is unplugged. | 1 (ICS has a single private connection) |

Command-line: `--mode=direct` or `--mode=reverse` (also accepts `direct-usb`, `reverse-tethering`, `ics`). An explicit `--mode=` overrides the saved selection. In headless launches (`--hidden --stop-event=<name>`, used by DYSEKT) the default is **Direct USB** regardless of the saved GUI selection, so a headless start never turns on internet sharing unless `--mode=reverse` is passed. An unknown `--mode=` value aborts startup instead of guessing.

## USB control path

The application no longer uses `libusb-win32`, `libusb0.sys`, or `install-filter.exe` for the Apple control transfers.

The mode-switch requests are sent through Microsoft's `Winusb.sys`/`Winusb.dll` stack using `WinUsb_ControlTransfer` on the MI_00 function. Microsoft documents WinUSB as a generic Windows USB function driver and documents `WinUsb_ControlTransfer` for control requests on the default endpoint. The implementation uses the same default-device control-transfer model for Apple's vendor requests.

This change is intentional: the previous libusb-win32 upper filter was a legacy kernel filter that remained attached to the Apple composite device while the NetAdapterCx NCM stack was repeatedly started/stopped. Repeated starts had produced `WDF_VIOLATION` BSODs with `libusb0.sys` present in the relevant driver-stack cluster.

## Build

Build target: **Windows 10 x64**. Development requires Visual Studio 2022 or the .NET 8 SDK.

```powershell
dotnet restore
dotnet publish .\src\iPhoneUsbShare.csproj -c Release -r win-x64 --self-contained true
```

The single-file executable is emitted under:

`src\bin\Release\net8.0-windows\win-x64\publish\iPhoneUsbShare.exe`

## Important testing note

USB descriptors and PnP behavior can differ by model/iOS version. The USB discovery and NCM-interface selection are capability-based: the Apple PID is discovered dynamically, and CDC-NCM is selected from the live USB descriptors rather than hard-coded to `MI_02`/`MI_04`.

The current confirmed hardware test is an **iPad Air 2 Wi-Fi-only**, which successfully established USB reverse tethering and produced an active Windows USB Ethernet adapter.

The implementation should therefore be tested across additional iPhone/iPad generations before making a universal compatibility claim.

## NCM selection

The tethering control function is identified as:

- CDC class `0x02`
- CDC subclass `0x0D`
- alternate setting 0
- interrupt IN notification endpoint present

The associated CDC Union descriptor identifies the data interface. The second Apple CDC-NCM-like function used by RemoteXPC lacks the interrupt notification endpoint and is rejected as an NCM tethering candidate.

## Windows NCM driver

The current NCM path identifies the tethering function from live descriptors, maps the interface number to the Windows `MI_xx` child devnode, and explicitly selects Microsoft's in-box `UsbNcm` driver through SetupAPI.

The application does not intentionally select Apple's historical `Netaapl` Ethernet driver for the NCM function.

## Compatibility

The implementation is expected to work on Apple devices that expose the required Apple mode-switch control protocol and CDC-NCM tethering function. The iPad Air 2 Wi-Fi-only test demonstrates that this works on at least one older Lightning-era iPad.

Do not treat the Apple PID alone as a compatibility guarantee. Newer iOS/iPadOS versions or USB-C devices may expose different configurations or interface layouts and should be tested against the capability checks.

## Current stability investigation

The first successful tethering runs were followed by repeated Windows `WDF_VIOLATION` BSODs. Crash analysis identified a relevant stack containing `libusb0.sys`, `NetAdapterCx.sys`, and `UsbNcm.sys` during repeated device lifecycle activity. The current revision removes libusb-win32 from the application and release package so that future testing can determine whether the crash was caused by that legacy filter's interaction with the modern NCM stack.

The next validation target is **repeated start/stop/reconnect testing without any `libusb0.sys` instance loaded**.

If a BSOD still occurs, retain the resulting dump and compare its bugcheck/module/stack with the previous dumps rather than changing the NCM descriptor-selection code blindly.

## License / attribution

The integration logic is based on the public MIT-licensed project:

https://github.com/0xbaksa/iphone-usb-reverse-tethering-windows

The application uses Microsoft-provided Windows components including WinUSB and the Windows NCM stack. No Apple software, private Apple components, or Apple kernel drivers are intended to be redistributed by this project.

## Getting the Windows EXE without building locally

This repository includes `.github/workflows/build-windows.yml`. On GitHub, open **Actions → Build Windows EXE → Run workflow**. GitHub's Windows runner compiles a self-contained x64 executable and uploads `iPhoneUsbShare-win-x64.zip` as an artifact. The package no longer contains the libusb-win32 runtime.

## Current NCM implementation note

The Windows USB stack may expose Apple's NCM union as ordinary `VID_05AC&PID_xxxx&MI_nn` child nodes rather than a PnP ID containing `CDC_0D`. The application therefore identifies NCM from live USB descriptors, maps the descriptor interface number to the corresponding PnP child, and selects Microsoft's `UsbNcm` driver through SetupAPI.

The first NCM control function is preferred because the observed Apple dual-function layout identifies the tethering function with the interrupt notification endpoint; the second function is the RemoteXPC channel and is not expected to become a usable NIC.
