# AppleNcm binding bring-up

This revision is specifically for the failure observed on MI_02: Windows kept
`netaapl64.inf` / `Netaapl` selected with Code 10 while the Microsoft
`usbncm.inf` candidate was only a lower-ranked compatible match.

The application now prefers a bundled `AppleNcm.inf` when present. It registers
the package, restricts SetupAPI's driver list to that INF, selects the driver by
the package's description (`Apple iPhone NCM Host Device`), and installs it on
the exact descriptor-identified NCM PDO. It does not remove Apple's driver
package globally.

The driver itself remains the minimal Microsoft-NCM descriptor-selection patch:
no forced USB configuration or alternate-setting rewrite was added.

## Expected decisive log

- `AppleNcm bring-up INF: ...AppleNcm.inf`
- `AppleNcm package registration exit code: 0`
- `SetupAPI AppleNcm candidate: description=Apple iPhone NCM Host Device ...`
- `AppleNcm SetupAPI driver selection ...: success`
- `service=AppleNcm` and `configError=0`
- `AppleNcm: descriptor selection control=2 data=3 unionData=3`

If package registration fails because of signing, that is now the binding blocker;
do not change USB descriptors or mode sequencing until signing is resolved.
