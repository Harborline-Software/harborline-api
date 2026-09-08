# ConfirmDialog — Interaction Contract

- **Component:** ConfirmDialog
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ConfirmDialog.Semantic.md) · [Accessibility](./ConfirmDialog.Accessibility.md) · [Styling](./ConfirmDialog.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ConfirmDialog.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Open / close

| Trigger | Effect |
| --- | --- |
| Host sets `open={true}` | Dialog mounts; focus moves to Cancel button |
| Cancel button click | `onOpenChange(false)` |
| Backdrop overlay click | `onOpenChange(false)` |
| Escape keydown (document listener) | `onOpenChange(false)` |
| Host sets `open={false}` | Dialog unmounts (`return null`) |

The dialog does not close itself on confirm — the host is responsible for
setting `open={false}` after the async `onConfirm` operation completes.

---

## 2. Confirm action

| Condition | Trigger | Effect |
| --- | --- | --- |
| `loading=false` | Confirm button click | `onConfirm()` |
| `loading=true` | Confirm button | Disabled — no event |

---

## 3. Loading state

When `loading={true}`:
- Both Cancel and Confirm buttons gain `disabled` attribute.
- Confirm button renders an animated spinner (SVG, `animate-spin`) before the label.
- Escape key still fires `onOpenChange(false)` (the handler remains registered);
  however, host should guard against closing mid-operation.

---

## 4. Focus management

On mount (`open` becomes `true`): `cancelRef.current?.focus()` — Cancel
button receives focus immediately. This is the safe default per ARIA dialog
pattern: focus should land on the safe action, not the destructive confirm.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-CD1 | Medium | No full focus trap — Tab can leave the dialog and interact with page content behind the backdrop | Accepted-risk M1 |
| G-CD2 | Low | Escape fires even when `loading=true` — host should guard against premature close during async operations | Accepted-risk M1 |
| G-CD3 | Low | No animation on open/close — dialog appears/disappears instantly | Deferred |
