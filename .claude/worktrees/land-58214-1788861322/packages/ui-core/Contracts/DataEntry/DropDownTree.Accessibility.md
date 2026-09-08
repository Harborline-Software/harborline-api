# DropDownTree — Accessibility Contract

- **Component:** DropDownTree
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DropDownTree.Semantic.md) · [Interaction](./DropDownTree.Interaction.md) · [Styling](./DropDownTree.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DropDownTree.tsx`
- **Catalog row:** #49 DropDownTree (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| *(none)* | Trigger `<div>` | No role, no aria-expanded — Gap G-DDT3 |
| *(none)* | Tree node `<div>` | No role — Gap G-DDT4 |

No ARIA roles are applied to any element in M1.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DDT3 | High | Trigger has no `role="combobox"`, no `aria-expanded`, no `aria-haspopup` | Accepted-risk M1 |
| G-DDT4 | High | Tree nodes have no `role="treeitem"`, no `aria-expanded` for nodes with children | Accepted-risk M1 |
| G-DDT5 | High | No keyboard access to tree — entirely mouse-only in M1 | Accepted-risk M1 |
