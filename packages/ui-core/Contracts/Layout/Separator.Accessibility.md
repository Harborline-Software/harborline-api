# Separator — Accessibility Contract

- **Component:** Separator
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Separator.Semantic.md) · [Interaction](./Separator.Interaction.md) · [Styling](./Separator.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Separator.tsx`
- **Catalog row:** #A13 Separator (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="none"` | Root `<div>` | Decorative (default); AT ignores |
| `role="separator"` | Root `<div>` | Semantic; AT announces structural boundary |
| `aria-orientation` | Root `<div>` | Set to `orientation` value when `decorative=false` |

---

## 2. Label variant

The labeled variant renders the label text in a `<span>`. This text is visible and part of the DOM. When `decorative=false`, AT reads the separator boundary and the label text is read as inline content.

---

## 3. No focus

Separator is not focusable. `tabIndex` is never set.
