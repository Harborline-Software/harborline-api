# Sheet — Interaction Contract

- **Component:** Sheet
- **ADR 0017 family:** Overlays
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sheet.Semantic.md) · [Accessibility](./Sheet.Accessibility.md) · [Styling](./Sheet.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A5 Sheet / SidePanel (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; shadcn Sheet baseline)

---

## 1. Open

Sheet opens when trigger is clicked or when `open=true` is set. Slide-in animation from configured `side`.

---

## 2. Close

Closes on:
- `SheetClose` button click
- Overlay click (when `modal=true`)
- Escape key press
- `onOpenChange(false)` from caller

---

## 3. Focus management

On open: focus moves to the first focusable element inside SheetContent (Radix Dialog behavior).

On close: focus returns to the trigger element.

---

## 4. Modal behavior

When `modal=true` (default): pointer events outside the Sheet are blocked; Sheet scrolls independently. When `modal=false`: background remains interactive.

---

## 5. Keyboard

- `Escape`: closes the Sheet.
- `Tab` / `Shift+Tab`: cycles through focusable elements inside the Sheet.
- Focus does not leave the Sheet while it is open (focus trap, Radix Dialog).

---

## 6. Known gaps

None identified for forward-spec. Validate against actual implementation in M2.
