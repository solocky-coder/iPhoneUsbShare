# AppleNcm.sys — first driver bring-up

This is the first real kernel-mode USB CDC-NCM host driver for iPhoneUsbShare.

## Design

The driver is based on Microsoft's open-source NCM host driver sample (`NCM-Driver-for-Windows`, `release_2004`) and keeps its KMDF + NetAdapterCx + NCM datapath. The Apple-specific change fixes function selection when an iOS device exposes two CDC-NCM functions: the tethering function has a CDC interrupt endpoint and its CDC Union descriptor points to the matching data interface; the second iOS 16+ NCM function is the RemoteXPC function and has no interrupt endpoint.

The first bring-up INF intentionally binds only the exact interface observed in the supplied ActivityLog:

`USB\\VID_05AC&PID_12AB&MI_02`

This is a **temporary bring-up match**, not the final compatibility policy. It is necessary to prove the kernel driver can replace `netaapl64.sys` on the observed Windows 10/iPad combination. Once the driver reaches D0 and creates a NetAdapter, the next step is to move the binding mechanism to a non-PID-based Apple CDC-NCM identification path.

## Build

The build script fetches the Microsoft source at a pinned commit/branch, applies the Apple function-selection patch and INF, then builds the host project with the Windows WDK. The output is renamed to `AppleNcm.sys` and the package is emitted as an unsigned development package.

For a normal Windows 10 x64 machine, install only in a test-signing/development environment until the package is signed appropriately.

## Important

This package is intentionally a driver bring-up. It does not modify the iPhone USB mode state, registry driver ranking, or AppleLowerFilter. The existing C# application remains responsible for switching the device into NCM mode and for ICS.
