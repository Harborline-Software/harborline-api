# @harborline-software/api-contracts

Shared contract package for Harborline. Existing domain namespaces remain TypeScript compatibility
surfaces; the app/Capability boundary is schema-first and generates TypeScript, C#, and Rust bindings.

## Namespaces

| Namespace   | Types exported | Source of truth |
|-------------|----------------|-----------------|
| `property`  | `Property`, `Unit`, `OccupancyStatus`, `RentStatus`, `RentRollRow` | Bridge property endpoints |
| `accounting`| `LedgerEntry`, `JournalEntry`, `BankTransaction`, `PLSummary`, `PLLineItem`, `OutstandingInvoice` | Bridge accounting endpoints |
| `tenant`    | `Tenant`, `Lease`, `PaymentRecord`, `MessageThread` | Bridge tenant endpoints |
| `sync`      | `SyncStatus`, `OfflineQueueEntry`, `ConflictRecord` | Offline-first sync layer |
| `integrations` | `IntegrationAtlasView`, `IntegrationCategory`, ... | ADR 0067 |
| `system-requirements` | `SystemRequirementsResult`, `OverallVerdict`, ... | ADR 0063 |
| `bundles`   | `BusinessCaseBundleManifest`, `BundleCategory`, `BundleStatus`, `DeploymentMode`, `ProviderCategory`, `ProviderRequirement`, `MinimumSpec` | ADR 0007 + A1 |
| `protocol` | `CapabilityPort`, `HarborlineHostPort`, `HarborlineApplicationPort` and their DTOs | `protocol/manifest.json` + JSON Schema 2020-12 |

## Harborline App and Capability protocol

Import the TypeScript projection from `@harborline-software/api-contracts/protocol`. C# consumers reference
`Harborline.Contracts.csproj`; Rust consumers depend on `rust/Cargo.toml`. None of those projections
is canonical—the manifest and schema are.

Change the schema first, regenerate all projections, then run every lane's fixture suite.

The `pnpm carrier-contracts:*` scripts this section used to name were defined in the source
repository's root `package.json`. harborline-api has no root `package.json` and no pnpm workspace,
so those commands do not exist here. Run the generator and each lane directly:

```bash
node tooling/harborline-contract-codegen/generate.mjs           # regenerate all four outputs
node tooling/harborline-contract-codegen/generate.mjs --check   # fail if any output is stale
pnpm --dir packages/contracts install --frozen-lockfile       # TypeScript dependencies
pnpm --dir packages/contracts test                           # TypeScript lane
dotnet test packages/contracts/tests/Harborline.Contracts.Tests.csproj   # C# lane
cargo test --manifest-path packages/contracts/rust/Cargo.toml            # Rust lane
```

### Which lanes are gated

All three, plus the staleness check, in the `protocol-lane-conformance` job of
`.github/workflows/packages.yml`.

That job is recent. Before it, CI ran only `dotnet test Harborline.Api.slnx` and
`eng/verify-packages.sh` — no Node, no cargo — so the lane that serves the API was gated but never
measured against the contract, while the two lanes that did measure it ran nowhere. A stale
regeneration, or one projection drifting from the schema, shipped green.

The job runs every lane even when an earlier one fails, because the useful signal from a
half-regenerated schema change is *which* lanes disagree: one red lane means a broken projection,
three red lanes mean the generator was never re-run.

### Keeping the model lists from drifting

The Rust harness hand-maintains two parallel model lists (`parse_model` and `is_known_model` in
`rust/src/lib.rs`). Both drifted — they stopped at 39 entries when the protocol grew to 41 — and
the lane was failing on `LastPeerExchange` and `SyncRecency` while the generated structs were
correct all along.

The C# runner resolves models by reflection against the manifest instead, so a newly declared model
cannot silently go unexercised: it fails naming the type the projection is missing. The TypeScript
lane checks membership against the schema's `$defs`. Prefer a derived list over a transcribed one.

## Bundle-manifest namespace — drift discipline

The `bundles` namespace mirrors the C# record
`Harborline.Api.Foundation.Catalog.Bundles.BusinessCaseBundleManifest`
(canonical source: `packages/foundation-catalog/Bundles/BusinessCaseBundleManifest.cs`).

**Drift discipline:**

- The C# record is canonical. Any field additions, type changes, or removals to the C# record
  MUST be reflected in `src/bundles.ts` before the change ships.
- The fixture-roundtrip test at `src/__tests__/bundles.test.ts` loads each of the 5 canonical
  `*.bundle.json` files from `packages/foundation-catalog/Manifests/Bundles/` and asserts shape
  conformance against the TypeScript interfaces. This test is the primary CI-time drift detector.
- Rust consumers (Harborline Toolbox) carry a serde struct mirror in that repository's desktop `src-tauri/src/bundles.rs`.
  Rust drift is caught by that repo's `cargo test` against the same fixture files.

All three mirrors (TypeScript, Rust, C#) must remain in sync. When the C# record changes,
update TypeScript here + Rust in Harborline Toolbox, and ensure the fixture JSON files are also updated.

## Usage

```ts
import type { BusinessCaseBundleManifest } from '@harborline-software/api-contracts'
```

## Development

```bash
npm run build       # compile TypeScript
npm run typecheck   # type-check without emit
npm test            # run vitest suite (includes bundle fixture-roundtrip)
```

## Installing from GitHub Packages

The package publishes to the organisation's npm registry at the same version as the NuGet packages. Add the scope to the consuming project's `.npmrc` and authenticate with a token that has `read:packages`:

```
@harborline-software:registry=https://npm.pkg.github.com
//npm.pkg.github.com/:_authToken=${NODE_AUTH_TOKEN}
```

Then `pnpm add @harborline-software/api-contracts@<version>`.
