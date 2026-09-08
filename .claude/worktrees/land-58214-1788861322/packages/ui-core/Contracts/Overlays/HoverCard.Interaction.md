# HoverCard — Interaction Contract

- **Component:** HoverCard
- **ADR 0017 family:** Overlays
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./HoverCard.Semantic.md) · [Accessibility](./HoverCard.Accessibility.md) · [Styling](./HoverCard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A2 HoverCard (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix @radix-ui/react-hover-card baseline)

---

## 1. Open triggers

Card opens after `openDelay` ms when pointer enters the trigger. Delay prevents accidental hover-opens during quick pointer movements.

---

## 2. Close triggers

Card closes after `closeDelay` ms when:
- Pointer leaves both the trigger AND the card content area.
- Focus leaves the HoverCard (for keyboard-initiated state).

The close delay allows the user to move the pointer from trigger to card without dismissal.

---

## 3. Grace period

Pointer can move between trigger and card (even briefly leaving both) without closing — Radix implements a pointer position grace triangle.

---

## 4. Keyboard behavior

HoverCard is purely pointer-driven. Keyboard users cannot open it via focus alone (per Radix default behavior). Callers requiring keyboard-accessible supplemental info should use Tooltip or Popover instead.

---

## 5. Known gaps

None identified for forward-spec. Validate against actual implementation in M2.
