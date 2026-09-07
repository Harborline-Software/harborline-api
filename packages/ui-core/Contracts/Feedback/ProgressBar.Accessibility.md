# ProgressBar — Accessibility Contract

- **Component:** ProgressBar
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ProgressBar.Semantic.md) · [Interaction](./ProgressBar.Interaction.md) · [Styling](./ProgressBar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ProgressBar.tsx`
- **Catalog row:** #100 ProgressBar (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="progressbar"` | Container `<div>` | WAI-ARIA progressbar widget |
| `aria-valuenow` | Container `<div>` | Current percentage (clamped); omitted when indeterminate |
| `aria-valuemin={0}` | Container `<div>` | Always 0 |
| `aria-valuemax={100}` | Container `<div>` | Always 100 |
| `aria-label` | Container `<div>` | Explicit prop, string label, or fallback `"Progress"` |

---

## 2. ChunkProgressBar

Same ARIA structure as ProgressBar — `role="progressbar"`, `aria-valuenow`, `aria-valuemin`,
`aria-valuemax`. It accepts an explicit `aria-label` and otherwise uses `"Progress"`.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PB1 | Medium | Boolean labels left the progressbar unnamed | Resolved 2026-07-15: every progressbar has an explicit or fallback accessible name |
| G-PB2 | Low | ChunkProgressBar was unnamed by default | Resolved 2026-07-15: explicit labels are supported and the fallback is `"Progress"` |
