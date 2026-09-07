# Clean-room attribution — ERPNext extraction adapter

Per ADR 0100 C4 (read-only clean-room source posture) and the predecessor
migration spec §9.5.

## What the extraction adapter reads

The `MariaDbDumpSourceReader` reads the **data format** of an ERPNext / Frappe
MariaDB dump only:

- table names (`tab<DocType>`),
- column names (from `CREATE TABLE`),
- row values (from `INSERT INTO ... VALUES`).

These are the public, observable data-interchange shape — the rows a user's data
occupies, as documented by ERPNext's public DocType field labels and API docs.

## What it does NOT do (clean-room boundary)

- It derives **no code** from ERPNext or the Frappe Framework — no controllers,
  validators, workflow logic, server scripts, or DocType-definition JSON
  (spec §9.1 / §9.2).
- It **never connects** to a live Frappe / ERPNext runtime, never opens a network
  connection, and never executes SQL — it parses a static dump file offline
  (ADR 0100 C4 / C6; C-CLEANROOM (d)).
- It **never writes back** to the source — strictly read-only (C-CLEANROOM (a)/(c)).

## Upstream license

ERPNext and the Frappe Framework are projects of **Frappe Technologies Pvt. Ltd.**,
licensed under the **GNU General Public License v3.0 (GPLv3)**.

This adapter is **format-reference-only**: no GPLv3 code is derived, copied, or
linked. Harborline Software's importer code is **Apache-2.0-licensed**
(see repo-root `Directory.Build.props` `PackageLicenseExpression`).
