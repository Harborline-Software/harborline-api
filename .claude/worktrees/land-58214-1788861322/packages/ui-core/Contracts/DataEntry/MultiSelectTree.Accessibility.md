# MultiSelectTree — Accessibility Contract

- **Component:** MultiSelectTree
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MultiSelectTree.Semantic.md) · [Interaction](./MultiSelectTree.Interaction.md) · [Styling](./MultiSelectTree.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiSelectTree.tsx`
- **Catalog row:** #87 MultiSelectTree (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| *(none)* | Trigger `<div>` | No role — Gap G-MST3 |
| `<input type="checkbox">` | Each node | Native checkbox; keyboard focusable |
| `disabled` | Node checkboxes | When `item.disabled=true` |
| `<button type="button">` | Expand/collapse | `text-xs text-muted-foreground w-3` |
| `<input type="checkbox">` | Select All | Native checkbox |

---

## 2. Checkbox accessibility

Native `<input type="checkbox">` elements provide `role="checkbox"` and keyboard support natively. However, they have no associated label text — they rely on adjacent `<span>` text, which is not linked via `id`/`htmlFor`. AT may not announce the item text. Gap G-MST4.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-MST3 | High | Trigger has no role, aria-expanded, or aria-haspopup | Accepted-risk M1 |
| G-MST4 | High | Node checkboxes have no label association (no htmlFor/id link to item text) | Accepted-risk M1; AT reads checkbox without a label name |
| G-MST5 | High | No tree role on dropdown container — AT cannot navigate as a tree | Accepted-risk M1 |
