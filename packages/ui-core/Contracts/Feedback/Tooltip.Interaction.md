# Tooltip — Interaction Contract

- **Component:** Tooltip
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Tooltip.Semantic.md) · [Accessibility](./Tooltip.Accessibility.md) · [Styling](./Tooltip.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Tooltip.tsx`
- **Catalog row:** #140 Tooltip (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
HIDDEN (visible=false)
  → mouseEnter on wrapper → start delayDuration timer → (after delay) → VISIBLE
  → focus on wrapper → start delayDuration timer → (after delay) → VISIBLE

VISIBLE (visible=true)
  → mouseLeave on wrapper → cancel timer, setVisible(false) → HIDDEN
  → blur on wrapper → cancel timer, setVisible(false) → HIDDEN
```

---

## 2. Show delay

Tooltip appearance is delayed by `delayDuration` ms (default 700ms). On `mouseEnter` or `focus`, a `setTimeout` fires; if `mouseLeave`/`blur` fires before it resolves, the timer is cancelled.

This prevents tooltip flicker when the cursor passes over a trigger without pausing.

---

## 3. Hide behaviour

On `mouseLeave` or `blur`, the timer is cleared and `visible` is set to `false` immediately — no hide delay.

---

## 4. No keyboard shortcut

No keyboard shortcut to dismiss the tooltip. Tooltip hides naturally when the trigger loses focus.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TT1 | Medium | No portal rendering — tooltip may be clipped by `overflow: hidden` ancestors | Accepted-risk M1; hosts that need portal use Radix Tooltip directly |
| G-TT2 | Low | `skipDelayDuration` not implemented | Accepted-risk M1; prop accepted for API forward-compatibility |
| G-TT3 | Low | Tooltip disappears if cursor moves from trigger to tooltip bubble | Accepted-risk M1; tooltip is informational-only, not interactive |
