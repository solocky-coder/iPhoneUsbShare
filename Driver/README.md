# AppleNcm.sys — minimal function-selection bring-up

This build is a deliberately small modification of Microsoft's NCM host driver
sample (`NCM-Driver-for-Windows`, `release_2004`). It does **not** replace
`EvtDevicePrepareHardware` or invent a new USB configuration sequence.

## What changed

The Microsoft sample's descriptor scan can let the second iOS 16+ NCM-like
function overwrite the first one. Apple devices observed by iPhoneUsbShare
expose:

- control interface 2, CDC NCM, one interrupt endpoint, Union 2 -> 3
- data interface 3, alternate 1, two bulk endpoints
- control interface 4, CDC NCM, no interrupt endpoint, Union 4 -> 5
- data interface 5, alternate 1, two bulk endpoints

The driver now:

1. selects the first CDC NCM communication interface that has one interrupt
   endpoint;
2. uses the CDC Union descriptor to associate that control interface with its
   subordinate data interface;
3. records only the first NCM/ECM functional descriptor after the selected
   function is identified;
4. maps the resulting USB interface numbers to the actual WDF interface objects
   using `WdfUsbInterfaceGetInterfaceNumber()`;
5. logs the WDF interface count, actual interface numbers, and setting counts.

## What deliberately did NOT change

- No wholesale `EvtDevicePrepareHardware` replacement.
- No assumption that WDF interface index 1 means USB interface 3.
- No forced alternate-setting 1 during initial configuration.
- Microsoft's existing `SelectConfiguration()` / `SelectSetting()` lifecycle is
  retained.
- No registry ranking hack.
- No unsigned companion INF.
- No AppleLowerFilter modification.
- The C# application remains responsible for Apple USB mode switching and ICS.

The diagnostic output we need from the next test is:

```text
AppleNcm: descriptor selection control=2 data=3 unionData=3
AppleNcm: WDF interface count=N
AppleNcm: WDF interface[0] usbIf=... settings=...
...
```

The decisive success criterion remains:

`USB\\VID_05AC&PID_12AB&MI_02 -> AppleNcm.sys -> Device Started -> NetAdapter appears`

The INF still uses the exact observed MI_02 hardware ID temporarily. That is
only a first bring-up match; it is not the project's final compatibility rule.
