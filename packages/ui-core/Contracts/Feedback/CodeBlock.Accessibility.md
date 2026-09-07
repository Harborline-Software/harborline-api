# CodeBlock — Accessibility Contract

- **Component:** CodeBlock
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CodeBlock.Semantic.md) · [Interaction](./CodeBlock.Interaction.md) · [Styling](./CodeBlock.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/CodeBlock.tsx`
- **Catalog row:** (not-in-catalog) CodeBlock (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `aria-label={copied ? 'Copied' : 'Copy code'}` | Copy `<button>` | State-dependent accessible label |
| `aria-hidden="true"` | Copy/check SVG icons | Decorative |

No `role` or ARIA on the code block container — it is a generic content region.

---

## 2. Code content

`<pre>` → `<code>` structure for correct AT reading of code content. Line numbers are `select-none` spans (excluded from selection but not `aria-hidden` — may be read by AT).

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CBLK2 | Medium | Line number spans are not `aria-hidden` — AT may read `"1 2 3 4..."` before the code content | Accepted-risk M1 |
| G-CBLK3 | Low | No `aria-label` or `aria-labelledby` on the code block container — `filename` is not linked via ARIA | Accepted-risk M1 |
