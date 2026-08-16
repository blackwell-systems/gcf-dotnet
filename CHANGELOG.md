# Changelog

## v0.2.1 (2026-08-15)

- Decode: quoted-key/array-value round-trip (SPEC 4.2).

## v0.2.0 (2026-08-14)

- **Numeric domain (spec v3.5.3, SPEC 2.3.2).** Specifies the canonical numeric domain as signed `int64` for integers and IEEE-754 double for non-integers. Earlier versions left integers beyond the double-exact range (2^53) to the host numeric type; this version parses integer literals to an exact `int64` on decode and on the JSON-to-value bridge, returns an out-of-range error for a value outside `int64` on both decode and encode, and models larger values (unsigned-64 identifiers, exact decimals) as strings. Canonical number formatting aligns to the domain: a double at or above 2^53 renders in exponent notation. Verified against new `numbers/017-024` and `errors-v2/041-042` conformance fixtures and the cross-SDK differential fuzz. Adds `EncodeException` for an out-of-domain value on encode, symmetric with `DecodeException` on decode.

## v0.1.2 (2026-08-10)

- **Losslessness fix (spec v3.5.2, SPEC 2.3/2.4).** The number grammar and numeric-like patterns now pin digits to ASCII `[0-9]`. .NET's `Regex` matches `\d` in Unicode mode (`\p{Nd}`), so a value like `1.٥` (ASCII `1`, `.`, U+0665) was classified as number-shaped and quoted on encode, where the ASCII SDKs leave it bare: a byte-identity divergence across the fleet. (Decode was unaffected, since `double.TryParse` with `InvariantCulture` rejects non-ASCII digits.) `\d` is replaced with `[0-9]`. Verified against new `scalar/029-031` and `decode/007` conformance fixtures and the cross-SDK differential fuzz.

## v0.1.1 (2026-08-09)

- **Fixed (losslessness):** a field name or scalar value ending in a newline (e.g. a key `"x\n"`) was mis-classified and emitted **unquoted**, splitting the wire. In .NET, regex `$` also matches just before a trailing newline, so `^...$` wrongly accepted such strings as bare keys / numbers. Anchored the scalar/key/number regexes with `\A ... \z` (strict end of string) to match the Go/Rust references. Caught by the property fuzz.
- **Added:** `gcf` command-line tool (`BlackwellSystems.Gcf.Cli`) with `encode` / `decode` / `encode-generic` / `decode-generic` / `stats`, matching the other SDKs' CLI surface. Install with `dotnet tool install -g BlackwellSystems.Gcf.Cli`.
- Property fuzz (idempotent-decode + delta round-trip) and cross-format fuzz (JSON / YAML / TOML / CSV); 33M+ round-trips clean. .NET added to the cross-language conformance matrix and the cross-SDK differential fuzz.

## v0.1.0 (2026-08-09)

- First release of the .NET / C# SDK. Full GCF **generic** and **graph** profiles, delta encoding (both profiles), session deduplication, streaming, and the re-anchor session helper.
- **Zero runtime dependencies**, multi-targeting `netstandard2.0` and `net8.0` (runs on .NET Framework 4.6.1+ through modern .NET). Passes the full cross-SDK conformance suite, byte-identical to the Go, TypeScript, Python, Rust, Swift, and Kotlin implementations.
- Raw-bytes decode entries (`Gcf.DecodeGeneric(byte[])` / `Gcf.Decode(byte[])`) enforce UTF-8 validity at the byte boundary (the spec's `invalid_utf8`), since .NET strings are UTF-16.
