# Breadcrumb — Accessibility Contract

- **Component:** Breadcrumb
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Breadcrumb.Semantic.md) · [Interaction](./Breadcrumb.Interaction.md) · [Styling](./Breadcrumb.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/Breadcrumb.tsx`
- **Catalog row:** #14 Breadcrumb (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `<nav aria-label="Breadcrumb">` | Root nav | Named navigation landmark |
| `<ol>` | Item list | Ordered list (conveys hierarchy) |
| `aria-current="page"` | Current item `<span>` | Marks the current page |
| `aria-hidden="true"` | Separator `<span>` | Decorative separator hidden from AT |

---

## 2. Keyboard

All links (`<a>`) are natively keyboard-focusable. The current item `<span>` and separator `<span>` are not focusable.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-BC1 | Low | `aria-label="Breadcrumb"` is hardcoded — cannot be localized | Accepted-risk M1 |
