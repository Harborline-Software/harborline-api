# FieldArray — Interaction Contract

- **Component:** FieldArray
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FieldArray.Semantic.md) · [Accessibility](./FieldArray.Accessibility.md) · [Styling](./FieldArray.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FieldArray.tsx`
- **Catalog row:** #54 FieldArray (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Operations

| Operation | Implementation | Effect |
|---|---|---|
| `append(value?)` | `setFields(f => [...f, { id: uid() }])` | Adds one field entry at the end |
| `remove(index)` | `setFields(f => f.filter((_, i) => i !== index))` | Removes entry at index |
| `move(from, to)` | Splice from index and insert at to index | Repositions an entry |

The `value` argument to `append` is accepted but ignored in M1 — caller manages actual field values.

---

## 2. No own user interaction

`FieldArray` has no interactive elements. All interaction is initiated by the caller via the `ops` object.

---

## 3. ID stability

Each field's `id` is generated once at creation and never changes. IDs increment from a module-level counter — they are stable within a page load but reset on hot-reload (a known dev-mode quirk).

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FA1 | Low | Module-level UID counter means IDs reset on hot-reload and collide across FieldArray instances after many reloads | Accepted-risk M1; stable enough for prod; use crypto.randomUUID for production hardening |
