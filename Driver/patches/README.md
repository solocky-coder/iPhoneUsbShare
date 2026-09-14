# Apple NCM source modification

The build script applies the Apple-specific `host/device.cpp` modification directly after cloning Microsoft's `release_2004` NCM source. This avoids brittle CRLF/unified-patch handling on Windows runners.

The modification:
- selects the first CDC-NCM control interface with a notification interrupt endpoint;
- parses the CDC Union Functional Descriptor (`0x24/0x06`);
- binds the selected control interface to its union slave data interface;
- ignores later NCM-like functions without the interrupt endpoint.
