# Popover — Accessibility Contract

- **Component:** Popover
- **ADR 0017 family:** Overlays
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Popover.Semantic.md) · [Interaction](./Popover.Interaction.md) · [Styling](./Popover.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Popover.tsx`
- **Catalog row:** #98 Popover (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA (from Radix UI)

| Attribute | Element | Value |
|---|---|---|
| `aria-haspopup="dialog"` | `PopoverTrigger` | Radix auto-applied |
| `aria-expanded` | `PopoverTrigger` | Open/closed state |
| `aria-controls` | `PopoverTrigger` | Points to popover content |
| `role="dialog"` | `PopoverContent` | Radix auto-applied |
| `aria-modal="true"` | `PopoverContent` | Radix auto-applied |

Callers should add `aria-label` or `aria-labelledby` to `PopoverContent` so AT announces the dialog purpose.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PPOVR1 | Medium | No default `aria-label` on `PopoverContent` — callers must add it explicitly | Accepted-risk M1 |
