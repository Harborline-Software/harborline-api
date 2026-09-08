# SplitButton — Accessibility Contract

- **Component:** SplitButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SplitButton.Semantic.md) · [Interaction](./SplitButton.Interaction.md) · [Styling](./SplitButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/SplitButton.tsx`
- **Catalog row:** #124 SplitButton (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `<button type="button">` | Primary action button | Text from `label` prop |
| `<button type="button">` | Caret button | Icon-only; requires `aria-label` |
| `aria-label="More options"` | Caret button | Screen reader label |
| `aria-haspopup="menu"` | Caret button | Indicates menu popup |
| `aria-expanded={open}` | Caret button | Reflects dropdown state |
| `role="menu"` | Dropdown container | WAI-ARIA menu role |
| `role="menuitem"` | Each option | Standard menu item role |
| `aria-disabled="true"` | Disabled option | Marks disabled options |

---

## 2. Loading state accessibility

When `loading=true`, both buttons are `disabled` (native HTML). The spinner SVG has no accessible text in M1 — loading state is not announced to AT.

---

## 3. Focus management

Caret button is separately focusable from the primary button (two tab stops). When dropdown closes via Escape, focus returns to caret button.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SB3 | High | `aria-controls` not set on caret button | Accepted-risk M1 |
| G-SB4 | High | Dropdown items do not receive focus — keyboard navigation within menu not implemented | Accepted-risk M1 |
| G-SB5 | Medium | Loading spinner has no `aria-label` or `aria-live` region — AT users not informed of loading state | Accepted-risk M1 |
