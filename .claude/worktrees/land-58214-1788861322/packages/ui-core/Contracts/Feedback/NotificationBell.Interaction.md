# NotificationBell — Interaction Contract

- **Component:** NotificationBell
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NotificationBell.Semantic.md) · [Accessibility](./NotificationBell.Accessibility.md) · [Styling](./NotificationBell.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/NotificationBell.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Bell button toggle

| Trigger | Effect |
| --- | --- |
| Bell button click | `open` toggles (`true ↔ false`) |
| Click outside component | `open → false` (mousedown listener on `document`) |

---

## 2. Panel interactions

| Trigger | Effect |
| --- | --- |
| Notification item click | `item.onClick?.()` then `setOpen(false)` |
| "Mark all read" click | `onMarkAllRead()` |
| "View all" click | `onViewAll?.()` then `setOpen(false)` |

---

## 3. Outside-click dismiss

A `mousedown` event listener on `document` checks if the click target is
outside `containerRef`. If so, `setOpen(false)`. The listener is attached
only when `open=true` and removed on cleanup.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-NB1 | Medium | No Escape key handling — keyboard users cannot close the panel with Escape | Accepted-risk M1 |
| G-NB2 | Medium | No focus trap in the panel — Tab can leave the panel without closing it | Accepted-risk M1 |
| G-NB3 | Low | "Mark all read" does not close the panel — it remains open; this may be intentional to show the updated read state | Accepted-risk M1 |
