# iPhoneUsbShare — AppleNcm driver bring-up

This revision adds the first real Windows kernel-mode USB CDC-NCM host driver component.

The driver is based on Microsoft's open-source NCM host driver sample, using KMDF + NetAdapterCx and the NCM NTB datapath. Microsoft documents that NetAdapterCx is available to KMDF NIC drivers starting with Windows 10 version 2004, and Microsoft's NCM repository describes its host driver as the basis for the Windows 11 UsbNcm implementation.

The Apple-specific driver change is important for this project: iOS 16+ can expose two NCM-like functions. The tethering function has an interrupt endpoint and a CDC Union descriptor pairing control interface 2 with data interface 3; the second RemoteXPC function has no interrupt endpoint. The driver therefore selects the interrupt-bearing NCM control function and follows its Union descriptor instead of pairing the last NCM control interface with the last data interface.

## First bring-up limitation

The first INF deliberately matches the exact interface observed during the current hardware test (`USB\\VID_05AC&PID_12AB&MI_02`). This is a driver bring-up gate so that we can prove the kernel/data path independently of Apple's `netaapl64.sys`. It is not intended to become the project's final compatibility policy.

Run the existing iPhoneUsbShare application first so the iPad is in NCM mode, then install the generated development package. The normal Windows driver-signing requirements still apply; this first package is intended for test/development signing.

## Sources

Microsoft NCM Driver for Windows: https://github.com/microsoft/NCM-Driver-for-Windows
Microsoft NetAdapterCx: https://learn.microsoft.com/en-us/windows-hardware/drivers/netcx/
