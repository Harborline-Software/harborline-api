# TextArea — Accessibility Contract

- **Component:** TextArea
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TextArea.Semantic.md) · [Interaction](./TextArea.Interaction.md) · [Styling](./TextArea.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/TextArea.tsx`
- **Catalog row:** #133 TextArea (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

Native `<textarea>` provides all semantic roles automatically. `...props` spread allows callers to pass `aria-label`, `aria-labelledby`, `aria-describedby`, `aria-invalid`, etc.

| Attribute | Element | Value |
|---|---|---|
| `id` | `<textarea>` | From `...props`; required for `<label htmlFor>` association |
| `name` | `<textarea>` | From `...props`; form participation |
| `disabled` | `<textarea>` | From `...props` |
| `readOnly` | `<textarea>` | From `...props` |
| `maxLength` | `<textarea>` | Native constraint |

---

## 2. Character counter

The counter `<span>` has `pointer-events-none` but no `aria-hidden`. AT may read the counter text as part of the textarea's surrounding content.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TA1 | High | `aria-invalid` not set when `error=true` — callers must add it via `...props` spread | Blocking-before-v1-ship — WCAG SC 4.1.2 (Level A) violation; must resolve before v1 ship |
| G-TA4 | Low | Counter span not `aria-hidden` — AT may read "14/500" while navigating | Accepted-risk M1 |
