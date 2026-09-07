# ChipList — Semantic Contract (stub)

- **Component:** ChipList
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted (stub — library-scope: planned; full contracts deferred)
- **Companion contracts:** none (stub — full contracts not yet authored)
- **Reference implementation:** none (no implementation yet)
- **Catalog row:** #26 ChipList (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec stub

---

## Purpose

**ChipList** is a multi-value chip input that allows users to enter, remove, and navigate a set of selected items represented as chips (tags). It supports keyboard entry (press Enter or comma to confirm a chip) and backspace-to-remove-last behavior.

Distinct from `Chip` (#25 — single static chip display element) and `MultiSelect` (#88 — dropdown-based multi-select). ChipList is a free-entry chip field where the user types values and they become chips on confirmation.

## Scope note

ChipList is planned for a future wave (`library-scope: planned`). Full 4-contract authoring is deferred until the component is prioritized for implementation. This stub documents the component identity and prevents the catalog from showing a phantom "full" spec-status.

## Related components

- **Chip** (#25) — single, non-interactive or removable chip display element
- **MultiSelect** (#88) — dropdown multi-select with predefined options
- **TagInput** — similar component; see `DataEntry/TagInput.Semantic.md` for an existing reference
