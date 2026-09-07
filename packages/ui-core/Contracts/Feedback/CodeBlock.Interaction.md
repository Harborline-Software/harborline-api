# CodeBlock — Interaction Contract

- **Component:** CodeBlock
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CodeBlock.Semantic.md) · [Accessibility](./CodeBlock.Accessibility.md) · [Styling](./CodeBlock.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/CodeBlock.tsx`
- **Catalog row:** (not-in-catalog) CodeBlock (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Copy action

Click copy button → `navigator.clipboard.writeText(code)` → set `copied=true` → call `onCopy?.()` → reset `copied=false` after 2000ms.

The button label changes from `"Copy"` to `"Copied"` (and `aria-label` from `"Copy code"` to `"Copied"`) during the 2s window.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CBLK1 | Low | When no header (no filename/language), the copy button is `absolute right-2 top-2` on the scroll `<div>` — the scroll `<div>` lacks `relative` explicitly (inherits from outer); layout is fragile if the outer wrapper has non-default `position` | Accepted-risk M1 |
