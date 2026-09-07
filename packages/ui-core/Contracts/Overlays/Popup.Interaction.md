# Popup — Interaction Contract

- **Component:** Popup
- **ADR 0017 family:** Overlays
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Popup.Semantic.md) · [Accessibility](./Popup.Accessibility.md) · [Styling](./Popup.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Popup.tsx`
- **Catalog row:** #99 Popup (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Open / close lifecycle

`open=true` triggers position computation. Popup renders after position is resolved. `open=false` unmounts the portal (returns `null`).

---

## 2. Click-outside dismiss

On mount, adds a `mousedown` listener to `document`. **IMPORTANT:** the listener MUST be deferred with `setTimeout(..., 0)` (or `requestAnimationFrame`) after the popup mounts — adding it synchronously means the same `mousedown` event that triggered `onOpenChange(true)` will also fire the listener and immediately close the popup. Race: trigger `mousedown` fires → popup mounts synchronously → immediate-listener fires on the already-propagating event → `onOpenChange(false)` is called. Net result: popup opens-and-closes in a single click. The `setTimeout(0)` defers the listener to the next macrotask, after the current click event finishes propagating.

On fire:

1. Resolves anchor element (`anchor instanceof HTMLElement ? anchor : anchor?.current`).
2. If `popupRef.current` and anchor are both resolved AND neither contains the event target → calls `onOpenChange(false)`.

Listener is removed on unmount.

---

## 3. Position recomputation

`useEffect` re-runs on `open`, `anchor`, `anchorAlign`, or `offset` changes. Calls `getBoundingClientRect()` + `scrollX`/`scrollY` on the resolved anchor. Stores `{ x, y }` in state. Position is NOT recalculated on window resize or scroll after initial open (known gap G-PPUP1).

---

## 4. Animation

When `animate=true` (default), renders with Tailwind animation classes `animate-in fade-in zoom-in-95 duration-150`. When `animate=false`, no animation classes.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-PPUP1 | Medium | Position not recalculated on scroll/resize after open — popup can drift if anchor moves | Accepted-risk M1 |
| G-PPUP2 | Low | No viewport boundary detection — popup can render partially off-screen | Accepted-risk M1 |
| G-PPUP3 | Low | No focus management on open — focus stays on anchor element | Accepted-risk M1 |
