# AppleNcm binding bring-up

This revision targets the observed `MI_02` failure where Windows keeps selecting
Apple's `netaapl64.inf` / `Netaapl` driver and reports Code 10 instead of loading
`AppleNcm.sys`.

The application may still contain SetupAPI selection logic, but the standalone
installer now deliberately separates package staging from driver selection.

## What the installer does

`install-apple-ncm.ps1`:

1. requires `AppleNcm.inf`, `AppleNcm.sys`, and `AppleNcm.cat` to exist together;
2. registers the package with `pnputil /add-driver ... /install`;
3. requires `devcon.exe` and runs `devcon update <AppleNcm.inf> <MI_02>` against
   the exact observed PDO;
4. enumerates that same instance with `pnputil /enum-devices ... /drivers`.

A successful `pnputil /add-driver` is **not** considered proof of binding. If
DevCon is unavailable, the script stops rather than giving a false-positive
bring-up result.

The default target is:

`USB\\VID_05AC&PID_12AB&MI_02`

Use `-DeviceInstanceId` for another descriptor-identified instance when testing
a different Apple device/PID.

## Build artifact contract

`build-driver.ps1` publishes a clean `Driver/artifacts/AppleNcm` directory containing:

- `AppleNcm.inf`
- `AppleNcm.sys`
- `AppleNcm.cat`
- `source-modification-notes.md`

The generated INF is an exact first-bring-up match for the observed MI_02
function. It is intentionally not the project's final compatibility rule.
The build now fails if the catalog or required INF service/hardware-id entries
are missing, rather than emitting a package that cannot be reliably installed.

The generated INF names `AppleNcm.cat`, matching its `CatalogFile` entry. This
keeps the staged package internally consistent for signing/installation.

## Decisive test

After the forced update, inspect the exact MI_02 instance. The desired state is:

- selected driver: `AppleNcm.inf`
- service: `AppleNcm`
- problem code: `0`
- `AppleNcm.sys` loaded

If package registration fails because of signing, signing policy is the blocker;
do not change USB mode sequencing or NCM parsing until that is resolved.

If binding succeeds, the next diagnostic target is driver execution:

```text
DriverEntry
EvtDeviceAdd
EvtDevicePrepareHardware
```

Then collect the existing WDF diagnostics, especially:

```text
AppleNcm: descriptor selection control=2 data=3 unionData=3
AppleNcm: WDF interface count=N
AppleNcm: WDF interface[0] usbIf=... settings=...
```

Do not assume WDF interface index 0/1 maps to USB interface 2/3; use
`WdfUsbInterfaceGetInterfaceNumber()` as already described by the driver README.
