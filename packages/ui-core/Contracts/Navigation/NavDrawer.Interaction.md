# NavDrawer — Interaction Contract

- **Component:** NavDrawer
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NavDrawer.Semantic.md) · [Interaction](./NavDrawer.Interaction.md) · [Accessibility](./NavDrawer.Accessibility.md) · [Styling](./NavDrawer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/NavDrawer.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Open/close triggers

| Trigger | Effect |
|---|---|
| `open` prop transitions to `true` | Drawer slides in; focus is moved to the drawer panel |
| Click close button (`✕`) | Calls `onClose()` |
| Click backdrop overlay | Calls `onClose()` |
| `Escape` (document-level) | Calls `onClose()` |
| Click a nav item | Calls `item.onClick?.()` then `onClose()` |

---

## 2. Focus management

When `open` transitions to `true`, `drawerRef.current?.focus()` is called via `useEffect`. The drawer root element has `tabIndex={-1}` to receive programmatic focus. This moves keyboard focus into the drawer immediately on open.

---

## 3. Keyboard listener lifecycle

When `open` becomes `true`:
- A document-level `keydown` listener for `Escape` is added.
- The listener is cleaned up when `open` becomes `false` or the component unmounts.

---

## 4. Backdrop

The backdrop is a full-screen `fixed` div rendered only when `open` is `true`. It carries `aria-hidden="true"` (decorative overlay). Clicking it calls `onClose()`.

---

## 5. Item click flow

1. User clicks an item (link or button).
2. `item.onClick?.()` fires (host-defined navigation or action).
3. `onClose()` fires automatically — the drawer closes after any item selection.

---

## 6. Known gaps

| Gap | Description |
|---|---|
| No focus trap | Focus can leave the open drawer by Tab; a focus trap is not implemented. Users can Tab to page content behind the drawer. |
| No scroll lock | `document.body` scroll is not locked when the drawer is open. Page behind the backdrop can be scrolled. |
| Focus not returned on close | When the drawer closes, focus is not explicitly returned to the trigger element that opened it. |
| Backdrop not animated | The backdrop appears/disappears instantly; no fade-in/out transition to match the drawer slide. |
