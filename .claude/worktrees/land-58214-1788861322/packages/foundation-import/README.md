# Harborline.Api.Foundation.Import

ERPNext-import primitives — the single owning home for the contract types the
ERPNext → Harborline importer build units (Workstream A: A1–A7) converge onto.
Ratified by **ADR 0100 (ERPNext Data Import Contract)**; this package is
**Workstream A0**.

It sits **below every `blocks-*` cluster** in the dependency DAG (a
foundation-tier package with no domain dependencies), so each cluster's importer
references the shared outcome type *up* the DAG with no peer-to-peer edge — this
is what collapses the D7 duplication (the `ImportOutcome<T>` type was copy-pasted
into 5 clusters: ledger / ar / ap / people-foundation / work-projects).

## What this package provides

### Outcomes (`Harborline.Api.Foundation.Import.Outcomes`)

- **`ImportOutcome<T>`** — the per-record outcome **discriminated union**
  (ADR 0100 C2; OQ-A ruling): `Inserted<T> | Updated<T> | Skipped<T> |
  Rejected(ImportFailure)`. Closed to this assembly (`private protected` ctor),
  so the orchestrator's exhaustive `switch` stays sound. The `Rejected` arm
  carries **no `T`** — a rejected record produced no local entity.
- **`ImportAction`** — the three happy-path markers (`Inserted | Updated |
  Skipped`). Deliberately has **no `Rejected` member** (adding one would silently
  weaken exhaustive switches). Replaces the per-cluster enum copies.
- **`ImportFailure`** — the **allowlisted, structured** reject projection
  (ADR 0100 C9; sec-eng amendment B): `{ ExternalRef, DocType, ReasonCode,
  FieldName?, RuleViolated? }`. Built from safe scalar identifiers only — there is
  structurally **no field** that can hold the raw source payload, party PII, or a
  monetary amount. `ImportRejectReason` is the bounded reason taxonomy.

### Census (`Harborline.Api.Foundation.Import.Census`)

- **`ImportCensus`** — the record-census-conservation primitive (ADR 0100 C2;
  .NET-arch amendment H). Counts each record into exactly one bucket
  (`Inserted`/`Updated`/`Skipped`/`Rejected`/`Halted`) and asserts
  `count(...) == count(source)`. Enforces "exactly three exits per record — no
  fourth exit" so **no financial record vanishes without a report line** (C5).
  Content-free: stores only counts.

### Extraction seam (`Harborline.Api.Foundation.Import.Extraction`)

- **`ISourceReader`** — the access-mode-agnostic **seam** (ADR 0100 C6).
  STRICTLY READ-ONLY: no write/update/delete method exists. Streams
  `SourceRow`s per DocType + a count for the census. Consumers (the upsert passes)
  depend only on this + `SourceRow`, never on a concrete adapter (C-MODE).
- **`SourceRow`** — a DocType-tagged, read-only column-name → value map. The
  seam's currency; the upsert pass maps it to a typed `Erpnext*Source` DTO.
- **`MariaDbDumpSourceReader`** — v1's **sole** adapter (C6 dump-only collapse).
  Parses a **static `mysqldump` `.sql` file offline** — no DB connection, no
  network, no live Frappe coupling — so the C4 read-only posture is uniformly
  provable. See `Extraction/LICENSE-ATTRIBUTION.md` (clean-room).
- **`SourceAccessMode`** — run-provenance descriptor; v1 = `MariaDbDump`.
  `RestApi` / `DocTypeCsv` are reserved, NOT-blessed future modes behind the seam.

### DI (`Harborline.Api.Foundation.Import.DependencyInjection`)

- **`AddHarborlineImportPrimitives()`** — registers the per-pass `ImportCensus`
  factory. The concrete dump reader is constructed per-run by the CLI (A7) from
  a `--source` path, so it is not DI-registered here.

## How A1–A7 build on this

| Unit | Uses |
|---|---|
| A1 (chart) | `ISourceReader` to stream `tabAccount` rows; `ImportOutcome<GLAccount>`; `ImportCensus` per pass |
| A2 (parties/periods/tax) | same seam for `tabCustomer`/`tabSupplier`/`tabFiscal Year`/…; `ImportOutcome<Party>` etc.; reject-path (`ImportFailure`) PII test lands here (C-LOG-REJECT) |
| A3 (opening balances) | `ImportOutcome<JournalEntry>`; `ImportRejectReason.ConstraintViolation` for imbalanced JEs |
| A4 (transactional) | `ImportOutcome<Invoice>` / `<Bill>` / `<PaymentEntry>`; per-record census |
| A5 (reconciliation linkage) | `ImportOutcome<T>` for link records |
| A6 (verify) | reads census totals for the per-DocType count reconciliation |
| A7 (CLI driver) | constructs `MariaDbDumpSourceReader.LoadAsync(--source)`; records `SourceAccessMode` provenance; threads `--target-tenant` |

The seven shipped upserters' convergence onto `ImportOutcome<T>` (and the
`TenantId`-first signature + first-class `ExternalRef` field) happens in the
A1–A4 PRs that already touch each importer — **not** in A0. A0 only establishes
the package, the DU, the seam, the census, and the dump adapter.
