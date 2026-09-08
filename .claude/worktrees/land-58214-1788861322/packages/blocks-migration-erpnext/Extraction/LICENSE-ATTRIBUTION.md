# Clean-room attribution — ERPNext extraction adapter (A0 host package)

Per ADR 0100 C4 (read-only clean-room source posture), the predecessor migration
spec §9.5, and mirroring `packages/foundation-import/Extraction/LICENSE-ATTRIBUTION.md`
(the primitive this adapter composes).

## What this package reads

The `MariaDbDumpExtractor` composes the `foundation-import`
`MariaDbDumpSourceReader`, which reads the **data format** of an ERPNext / Frappe
MariaDB dump only:

- table names (`tab<DocType>`, ERPNext v15 naming),
- column names (from `CREATE TABLE`),
- row values (from `INSERT INTO ... VALUES`),
- the parent/child correlation column (`parent`) used to reconstruct a logical
  document (header + child rows) by **in-process grouping** — never by a DB JOIN.

These are the public, observable data-interchange shape — the rows a user's data
occupies, as documented by ERPNext's public DocType field labels and API docs.
The `ErpnextDocTypeMap` pins the v15 `tab<DocType>` -> DTO routing; it is a
data-format reference table, not derived from Frappe code.

## What it does NOT do (clean-room boundary)

- It derives **no code** from ERPNext or the Frappe Framework — no controllers,
  validators, workflow logic, server scripts, or DocType-definition JSON
  (spec §9.1 / §9.2).
- It **never connects** to a live Frappe / ERPNext runtime, never opens a network
  connection, and never executes SQL. It does NOT restore the dump into a database
  engine and query it — it composes the `foundation-import` offline string-parse
  reader and reconstructs parent/child documents in managed memory (ADR 0100
  C4 / C6; C-CLEANROOM (d)). See the PR description's "restore-vs-streaming"
  reconciliation for why the in-process composition is the cleaner long-term seam
  than the design's restore-to-DB recommendation.
- It **never writes back** to the source — strictly read-only (C-CLEANROOM (a)/(c)).
- It **never logs PII / financial values / credentials** — only DocType names,
  the opaque `externalRef` (the ERPNext `name`), and counts (C9). A record that
  fails to map surfaces an allowlisted `ImportFailure` projection, never the raw
  row.

## Upstream license

ERPNext and the Frappe Framework are projects of **Frappe Technologies Pvt. Ltd.**,
licensed under the **GNU General Public License v3.0 (GPLv3)**.

This adapter is **format-reference-only**: no GPLv3 code is derived, copied, or
linked. Harborline Software's importer code is **Apache-2.0-licensed**
(see repo-root `Directory.Build.props` `PackageLicenseExpression`).
