# ActionSheet — Accessibility Contract

- **Component:** ActionSheet
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ActionSheet.Semantic.md) · [Interaction](./ActionSheet.Interaction.md) · [Styling](./ActionSheet.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/ActionSheet.tsx`
- **Catalog row:** #1 ActionSheet (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="dialog"` | Sheet container | Identifies as modal dialog |
| `aria-modal="true"` | Sheet container | Signals modal behavior to AT |
| `aria-label={title ?? 'Action sheet'}` | Sheet container | Accessible name |
| `aria-hidden="true"` | Backdrop `<div>` | Hidden from AT |
| `<button type="button">` | Each item | Native button role |
| `disabled` | Disabled item button | Native HTML disabled |
| `<button type="button">` | Cancel button | Native button role |

---

## 2. Focus management

When the sheet opens, focus is NOT automatically moved into it in M1. Focus trapping is also absent. This is a known gap.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-AS4 | High | No focus trap — background content remains reachable by keyboard | Accepted-risk M1; design assumes pointer/touch primary |
| G-AS5 | High | Focus not moved to sheet on open — screen reader users may not discover the sheet | Accepted-risk M1 |
| G-AS6 | Medium | No `aria-describedby` linking title to dialog | Accepted-risk M1; `aria-label` provides accessible name |
