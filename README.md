# gcf-dotnet

A zero-dependency .NET/C# implementation of **GCF (Graph Compact Format)**: a compact,
lossless, LLM-oriented serialization of structured data.

- **Zero external dependencies.** No NuGet packages at runtime; JSON parsing, hashing, and
  everything else uses in-box APIs (or hand-written code).
- **Multi-target.** `netstandard2.0` (runs on .NET Framework 4.6.1+, Unity, Mono) and `net8.0`.
- **Conformance-verified.** Passes the full shared cross-SDK conformance suite (264/264 v2
  fixtures), so it round-trips byte-identically to the Go, TypeScript, Python, Rust, Swift, and
  Kotlin SDKs.

## Install

```
dotnet add package BlackwellSystems.Gcf
```

## Generic profile

The generic profile encodes arbitrary structured data. The native value model is: `null`,
`bool`, `long`, `double`, `string`, `OrderedMap` (order-preserving object), and `IList` (array).

```csharp
using BlackwellSystems.Gcf;

var data = new OrderedMap
{
    ["users"] = new List<object?>
    {
        new OrderedMap { ["id"] = 1L, ["name"] = "Alice", ["active"] = true },
        new OrderedMap { ["id"] = 2L, ["name"] = "Bob",   ["active"] = false },
    },
};

string wire = Gcf.EncodeGeneric(data);
object? back = Gcf.DecodeGeneric(wire);   // lossless round trip
```

## Generic delta (evolving data over a session)

For data that is re-queried and changes over time, transmit only what changed:

```csharp
var set = new GenericSet("id", new[] { "id", "status" }, rows, name: "orders");
var session = new GenericDeltaSession(set, tool: "orders", ReanchorPolicy.SizeGuard());

string full = session.CurrentFull();          // send once to establish the base
var (wire, isFull) = session.Next(nextSet);   // compact delta (or periodic full re-anchor)
```

The consumer applies deltas with `Gcf.DecodeGenericDelta` + `Gcf.VerifyGenericDelta`, which
verifies the result against a content-addressed SHA-256 pack root.

## Graph profile (code intelligence)

```csharp
var payload = new Payload
{
    Tool = "context_for_task",
    Symbols = { new Symbol { QualifiedName = "pkg.Handler", Kind = "function", Score = 0.95, Provenance = "lsp" } },
    Edges = { new Edge { Source = "pkg.Handler", Target = "pkg.Store", EdgeType = "calls" } },
};
string wire = Gcf.Encode(payload);
Payload back = Gcf.Decode(wire);
```

Also supported: `Gcf.PackRoot`, `Gcf.EncodeDelta` / `Gcf.DecodeDelta` / `Gcf.VerifyDelta`,
`Gcf.EncodeWithSession` (bare `@N` references for previously-transmitted symbols), and the
incremental `StreamEncoder` (O(1) memory, deferred counts, `##! summary` trailer).

## License

MIT. Part of the GCF project; see [gcformat.com](https://gcformat.com).
