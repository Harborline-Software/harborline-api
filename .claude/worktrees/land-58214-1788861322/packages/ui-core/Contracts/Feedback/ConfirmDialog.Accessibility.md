# ConfirmDialog — Accessibility Contract

- **Component:** ConfirmDialog
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ConfirmDialog.Semantic.md) · [Interaction](./ConfirmDialog.Interaction.md) · [Styling](./ConfirmDialog.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ConfirmDialog.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `role="dialog"` | Dialog panel `<div>` | Marks as modal dialog for AT |
| `aria-modal="true"` | Dialog panel `<div>` | Tells AT to treat as modal (restrict virtual cursor) |
| `aria-labelledby="confirm-dialog-title"` | Dialog panel `<div>` | Points to the `<h2>` title element |
| `aria-describedby="confirm-dialog-desc"` | Dialog panel `<div>` | Points to description `<p>` (only when `description` is present) |
| `aria-hidden="true"` | Backdrop overlay `<div>` | Backdrop is decorative |
| `aria-hidden="true"` | Danger warning icon SVG | Icon is decorative |
| `aria-hidden="true"` | Loading spinner SVG | Spinner is decorative |

---

## 2. Focus management

On `open → true`: `cancelRef.current?.focus()` fires immediately via
`useEffect`. The Cancel button receives focus — the safe ARIA dialog default
(focus lands on the non-destructive action).

On `open → false`: focus returns to wherever it was before the dialog opened
is NOT explicitly managed by ConfirmDialog (gap G-CD4 below). The host is
responsible for returning focus to the trigger element.

---

## 3. Keyboard behavior

| Key | Effect |
| --- | --- |
| `Escape` | Fires `onOpenChange(false)` |
| `Tab` | Moves to next focusable element (NOT trapped — gap G-CD1) |
| `Space` / `Enter` on Cancel | Fires `onOpenChange(false)` |
| `Space` / `Enter` on Confirm | Fires `onConfirm()` |

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-CD1 | Medium | No focus trap — Tab can cycle to page content behind the backdrop | Accepted-risk M1 |
| G-CD4 | Low | No explicit focus-return on close — host must manage returning focus to the trigger element | Accepted-risk M1 |
| G-CD5 | Low | `aria-modal="true"` alone is insufficient in some older AT without a full focus trap | Accepted-risk M1 |
| G-CD6 | Low | Disabled state on loading buttons has no `aria-busy` or `aria-disabled` announcement | Accepted-risk M1 |
