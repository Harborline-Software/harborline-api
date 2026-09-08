# Harborline.Api.Blocks.Migration.Erpnext

A0 ERPNext extraction adapter — the cross-cluster host package for the
ERPNext → Harborline importer's **source-read half** (ADR 0100, Workstream A0).

## What this package provides

### The C6 access-mode seam (`Extraction/IErpnextSourceExtractor`)

The single typed interface A1–A6 depend on. Exposes ONLY read operations
returning the frozen `Erpnext*Source` DTOs per DocType family. A future REST
or CSV adapter implements this interface with **zero changes to A1–A6** — the
seam is the value.

### v1 implementation: `MariaDbDumpExtractor`

Composes the `foundation-import` `ISourceReader` / `MariaDbDumpSourceReader`
primitives to project `SourceRow`s into typed DTOs. The dump parsing, the
mysqldump dialect handling, the `tab<DocType>` discovery — all delegate to
`foundation-import#183`. This extractor adds only:

- **Row → DTO field mapping** (column name → typed property, via
  `ErpnextFieldReader`)
- **In-process parent/child JOIN reconstruction** (child rows pre-loaded and
  grouped by the Frappe `parent` column, then attached to each parent header as
  it streams)
- **USD-only guard** (ADR 0100 OQ-2; non-USD row → `ErpnextExtractionException`)
- **C5 DocType census** (`ReadInventoryAsync`)

### `ErpnextDocTypeMap` (versioned, v15-pinned)

The one authoritative shape map: `tab<DocType>` → `Erpnext*Source` DTO target,
plus the child-table and known-irrelevant allowlists. Pinned to ERPNext v15
(CIC-resolved 2026-05-29). An unknown DocType routes to the `_unmapped/`
census — **never silently dropped** (C5).

## DAG position

This package sits **ABOVE all `blocks-*` clusters** in the dependency graph
(it references seven cluster `Migration` DTO namespaces UP the graph; no
cluster depends on it). This mirrors where the A7 CLI host will sit. See ADR
0100 §§G and the design PR for the DAG argument.

## Streaming-vs-restore seam (v1 posture)

The A0 design (#194) recommended restoring the dump into a throwaway MariaDB
instance and issuing real SQL JOINs. v1 instead composes the already-shipped
`foundation-import` string-parse reader and reconstructs parent/child documents
**in managed memory** (group child rows by the `parent` column).

This keeps the `C-CLEANROOM "no DB connection / no network"` arch-test
**uniformly provable** for v1. The seam is below `ISourceReader`: swapping to a
future `RestoreToDbSourceReader : ISourceReader` is an additive,
A1–A6-invisible change. The .NET-architect council has been asked to adjudicate
the streaming-vs-restore design question explicitly — see the PR description for
the full trade-off analysis.

## Clean-room posture (C4, C9)

- **Read-only against source**: no `Write*`/`Update*`/`Delete*` method on
  `IErpnextSourceExtractor` or `ISourceReader`; an arch-test asserts this.
- **No DB connection / no network** in v1: `MariaDbDumpExtractor` composes the
  offline string-parse reader. C-CLEANROOM (d) arch-test asserts no extraction
  type references `HttpClient`, `MySqlConnection`, etc.
- **Dump file outside the repo tree**: the path is a CIC-supplied CLI flag /
  env var pointing outside the fleet tree. A `.gitignore` in this package
  defensively blocks `*.sql`, `*-dump.sql`, `import-source/`.
- **Logs emit only DocType, opaque `externalRef` (the ERPNext `name`), and
  counts** — never party PII, monetary values, credentials, or raw rows (C9).
- **Extraction failures**: an `ErpnextExtractionException` carries only
  `{DocType, ExternalRef, ReasonCode, FieldName?}` — structurally incapable of
  leaking C9-forbidden content.

## Where the dump file lives

The `mysqldump` `.sql` file is CIC's real financial books + PII. It **must not**
enter the repo tree. Supply it via:

```sh
--source-dump /secure/local/path/erpnext.sql   # A7 CLI flag (out of scope for this PR)
```

The `.gitignore` in this package directory blocks accidental in-tree copies as
a belt-and-suspenders backstop.

## Unit tests

Tests live in `tests/` and use **synthetic v15-shaped fixture SQL strings** —
no CIC data is required to build or run the test suite. The real dump is
RUN-time only (CIC-supplied at import execution).
