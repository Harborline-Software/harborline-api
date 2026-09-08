# Harborline App protocol contracts

`manifest.json` and `schemas/carrier-protocol.schema.json` are the canonical source for the
Harborline App, native host, and Capability boundaries. TypeScript, C#, Rust, and OpenAPI files under
this package are generated projections and must not be edited directly.

`manifest.json` remains the generator input and therefore lists covered operations only.
`boundary-inventory.json` is separate by design: it records the independently discovered runtime
surface, including deferred and blocked operations, without teaching code generation to accept an
operation that has no schema or adapter. After an intentional producer change, normalize it with
`node tooling/harborline-contract-codegen/boundary-inventory.mjs --write`. Newly discovered local-node
HTTP routes inherit the inventory's documented deferred policy; other new boundary kinds still
require explicit generator classification. The generator suite fails on any unlisted or stale
producer operation and on any deferred/blocked entry without a reason.

The earlier app checkout is not present in this repository. Its small `generate_handler!`
command-list fixture lives at
`tooling/harborline-contract-codegen/fixtures/harborline-generate-handler.rs`; discovery verifies that
fixture against the app's `src-tauri/src/lib.rs` whenever the real producer is available.

The manifest describes ports and authority ownership. JSON Schema describes serialized data. A
runtime implements a generated port through an adapter; no implementation language is privileged.

Top-level protocol models are closed by default. The only forward-compatible escape hatch is the
predeclared `extensions` object on these metadata containers: `RuntimeHealth`, `RuntimeCapability`,
`AnnounceResult`, `AddressResult`, `ResolutionResult`, `NodeStatus`, `DataLocationStatus`,
`DeviceCapabilityProfile`, and `HarborlineSyncStatus`. Its members are arbitrary JSON. Exact numeric
round-tripping is guaranteed only for values representable by the consuming language; in
particular, TypeScript cannot preserve JSON integers above `9007199254740991`. Use strings for
larger integers that must round-trip exactly. `Artifact` and `ProviderDescriptor` remain the two
intentional open top-level models:
the former is a polymorphic result slot and the latter carries provider manifests verbatim.

Adding a member inside `extensions` is protocol-minor. Adding a named top-level property to a
closed model remains protocol-major. Security-sensitive request models, including
`CapabilityInvokeRequest`, remain closed.

Run `npm run codegen` (in `packages/contracts`) after changing a schema and
`npm run codegen:check` in CI.

The Vitest suite also compiles the canonical schema with an AJV Draft 2020-12 validator and checks
every fixture in `fixtures/manifest.json` directly against its declared `$defs` model. Generated
projection tests do not replace this canonical-schema gate.

## Adapter rules

1. Add or change an operation in `manifest.json`; never start in a generated language file.
2. Give every operation a named request and response schema. Generic `execute`/`payload` command
   buses are intentionally forbidden.
3. Keep principal, session, signing, and authority evidence out of renderer request schemas. Mark
   host-owned context in the manifest and stamp it in the trusted adapter.
4. Add a fixture and regenerate TypeScript, C#, and Rust. All three fixture suites must pass.
5. Implement the generated port in the runtime and preserve the compatibility facade until callers
   migrate independently.

Current adapters are:

- Capability TypeScript shell: `apps/capability-host/src/protocol/capability-port-adapter.ts`;
- Harborline App React renderer to Tauri: the app's Tauri protocol adapter;
- Harborline App React renderer to local-node HTTP: the app's HTTP protocol adapter;
- Rust host: generated command constants consumed by the app's `src-tauri/src/lib.rs`;
- ASP.NET local-node: generated sync-status route constant consumed by
  `apps/local-node-host/Health/SyncStatusRoutes.cs`.

A MAUI host, Blazor renderer, C# Capability, Python worker, or another future implementation consumes the
same port and fixture corpus. It does not translate from the React or Tauri implementation.
