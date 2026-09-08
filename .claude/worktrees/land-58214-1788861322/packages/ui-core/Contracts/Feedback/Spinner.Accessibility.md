# Spinner — Accessibility Contract

- **Component:** Spinner
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Spinner.Semantic.md) · [Interaction](./Spinner.Interaction.md) · [Styling](./Spinner.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Spinner.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="status"` | `<svg>` | Polite live region announcing loading state |
| `aria-label={label}` | `<svg>` | Default: `"Loading…"` |

The SVG is announced by AT as a named status region.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SPNR1 | Low | `role="status"` on an SVG may not create a live region in all AT combinations — some AT require a `<div role="status">` wrapper to trigger live-region announcements | Accepted-risk M1 |
