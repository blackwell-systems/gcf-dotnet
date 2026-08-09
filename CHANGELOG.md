# Changelog

## v0.1.1 (2026-08-09)

- **Fixed (losslessness):** a field name or scalar value ending in a newline (e.g. a key `"x\n"`) was mis-classified and emitted **unquoted**, splitting the wire. In .NET, regex `$` also matches just before a trailing newline, so `^...$` wrongly accepted such strings as bare keys / numbers. Anchored the scalar/key/number regexes with `\A ... \z` (strict end of string) to match the Go/Rust references. Caught by the property fuzz.
- **Added:** `gcf` command-line tool (`BlackwellSystems.Gcf.Cli`) with `encode` / `decode` / `encode-generic` / `decode-generic` / `stats`, matching the other SDKs' CLI surface. Install with `dotnet tool install -g BlackwellSystems.Gcf.Cli`.
- Property fuzz (idempotent-decode + delta round-trip) and cross-format fuzz (JSON / YAML / TOML / CSV); 33M+ round-trips clean. .NET added to the cross-language conformance matrix and the cross-SDK differential fuzz.

## v0.1.0 (2026-08-09)

- First release of the .NET / C# SDK. Full GCF **generic** and **graph** profiles, delta encoding (both profiles), session deduplication, streaming, and the re-anchor session helper.
- **Zero runtime dependencies**, multi-targeting `netstandard2.0` and `net8.0` (runs on .NET Framework 4.6.1+ through modern .NET). Passes the full cross-SDK conformance suite, byte-identical to the Go, TypeScript, Python, Rust, Swift, and Kotlin implementations.
- Raw-bytes decode entries (`Gcf.DecodeGeneric(byte[])` / `Gcf.Decode(byte[])`) enforce UTF-8 validity at the byte boundary (the spec's `invalid_utf8`), since .NET strings are UTF-16.
