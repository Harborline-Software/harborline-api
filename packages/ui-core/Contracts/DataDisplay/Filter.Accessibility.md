# Filter — Accessibility Contract

- **Component:** Filter
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Filter.Semantic.md) · [Interaction](./Filter.Interaction.md) · [Styling](./Filter.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/Filter.tsx`
- **Catalog row:** #59 Filter (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| *(none)* | Container `<div>` | No role |
| *(none)* | Row `<div>` | No grouping role |
| `aria-label="Remove filter"` | Remove button | Accessible name for `×` icon button |
| Native `<select>` | Field + operator pickers | Browser-native accessible |
| Native `<input>` | Value inputs | Browser-native accessible |

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FT2 | High | No `<label>` association for field/operator/value selects — keyboard/AT users cannot identify what each control affects | Accepted-risk M1; row-position provides implicit context in visual layout |
| G-FT3 | Medium | No `role="group"` or `aria-label` on each filter row — individual rows are not semantically bounded | Accepted-risk M1 |
| G-FT4 | Low | "Add filter" button has no icon; text only — accessible but may be unclear out of context | N/A; text is explicit |
