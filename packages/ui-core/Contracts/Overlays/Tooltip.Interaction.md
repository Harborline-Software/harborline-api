# Tooltip — Interaction Contract

- **Component:** Tooltip
- **ADR 0017 family:** Overlays
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Tooltip.Semantic.md) · [Accessibility](./Tooltip.Accessibility.md) · [Styling](./Tooltip.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Tooltip.tsx`
- **Catalog row:** #140 Tooltip (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
HIDDEN
  → mouseenter or focus → start timer(delayDuration ms) → PENDING
  → (timer fires) → VISIBLE

PENDING
  → mouseleave or blur → cancel timer → HIDDEN

VISIBLE
  → mouseleave or blur → HIDDEN (immediate; no delay)
```

---

## 2. Timer mechanics

`show()`: `setTimeout(() => setVisible(true), delayDuration)`. Ref stores timer ID.
`hide()`: `clearTimeout(timerRef.current)` + `setVisible(false)`.

---

## 3. Escape key

Not implemented in M1. The WAI-ARIA tooltip pattern requires Escape to dismiss the tooltip on keyboard trigger.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TT1 | Medium | Escape key does not dismiss tooltip when shown via keyboard focus | Accepted-risk M1 |
| G-TT2 | Low | `skipDelayDuration` accepted but not implemented | Accepted-risk M1 |
| G-TT3 | Low | No viewport-edge detection — tooltip may clip at screen edges | Accepted-risk M1 |
