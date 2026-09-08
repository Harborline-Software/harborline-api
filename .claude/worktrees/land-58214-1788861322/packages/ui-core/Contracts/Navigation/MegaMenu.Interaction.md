# MegaMenu — Interaction Contract

- **Component:** MegaMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MegaMenu.Semantic.md) · [Interaction](./MegaMenu.Interaction.md) · [Accessibility](./MegaMenu.Accessibility.md) · [Styling](./MegaMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/MegaMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Trigger button interactions

| Trigger | Effect |
|---|---|
| Click trigger (closed) | Opens that trigger's panel; closes any previously open panel |
| Click trigger (open) | Closes the panel (toggle) |
| `Escape` (document-level listener while panel is open) | Closes the active panel |
| Click outside container (document `mousedown`) | Closes the active panel |

---

## 2. Panel link interactions

| Trigger | Effect |
|---|---|
| Click `<a>` link | Fires `link.onClick?.()`, closes panel, navigates (browser default) |
| Click `<button>` link | Fires `link.onClick?.()`, closes panel |

---

## 3. Event listener lifecycle

When a panel opens (`active` transitions from `null` to a trigger id):
- A document-level `keydown` listener for `Escape` is added.
- A document-level `mousedown` listener for outside-click is added.

Both listeners are cleaned up when the panel closes or the component unmounts (via the `useEffect` cleanup return).

---

## 4. Chevron indicator

Each trigger button renders a `▾` (down) or `▴` (up) glyph via `aria-hidden="true"` span indicating open/closed state. This is a visual affordance only.

---

## 5. Known gaps

| Gap | Description |
|---|---|
| No hover/focus open mode | The panel opens only on click; hover-open is not supported. Some MegaMenu patterns open on hover for pointer users. |
| No keyboard traversal within panel | Tab and arrow keys are not wired to traverse between columns or links within the open panel. Focus management is left to browser default Tab order. |
| Grid-cols dynamic class | `grid-cols-${N}` requires Tailwind safelist; values 2–4 must be explicitly listed or the layout may collapse to a single column. |
| No close on trigger blur | Tabbing away from the trigger does not close the panel; only Escape and outside-click do. |
