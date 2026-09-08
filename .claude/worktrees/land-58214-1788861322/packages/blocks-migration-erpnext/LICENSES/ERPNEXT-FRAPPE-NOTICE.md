# ERPNext / Frappe Framework — Attribution Notice

Per ADR 0100 C4 (read-only clean-room source posture) and the predecessor
migration spec §9.5.

## What this package references

`Harborline.Api.Blocks.Migration.Erpnext` reads the **data format** of an ERPNext /
Frappe MariaDB dump only — the public, observable data-interchange shape:

- Table names (`tab<DocType>`, ERPNext v15 naming convention)
- Column names (from `CREATE TABLE` definitions in the dump)
- Row values (from `INSERT INTO ... VALUES` statements)
- The `parent` correlation column used by Frappe's child-table pattern

The `ErpnextDocTypeMap` pins the v15 `tab<DocType>` → DTO routing. It is a
data-format reference table, not derived from Frappe application code.

## What it does NOT include

- **No Frappe / ERPNext application code** is copied, derived, linked, or
  incorporated — no controllers, validators, workflow scripts, server-side
  DocType definitions, or business logic. (Spec §9.1 / §9.2.)
- **No live connection** to a Frappe / ERPNext runtime. The adapter reads a
  static offline dump file.
- **No write-back** to the ERPNext source — strictly read-only.

## Upstream license

ERPNext and the Frappe Framework are open-source projects of
**Frappe Technologies Pvt. Ltd.**, licensed under the
**GNU General Public License v3.0 (GPLv3)**.

See: <https://github.com/frappe/frappe> and <https://github.com/frappe/erpnext>

## This package's license

Harborline Software's importer code in this package is **Apache-2.0-licensed**.
See `Directory.Build.props` (`PackageLicenseExpression = Apache-2.0`) at the repo root
and the root `LICENSE` file.

This attribution notice satisfies the clean-room documentation requirement of
ADR 0100 C4 (b) / spec §9.5.
