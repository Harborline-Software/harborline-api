# SplitButton — Interaction Contract

- **Component:** SplitButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SplitButton.Semantic.md) · [Accessibility](./SplitButton.Accessibility.md) · [Styling](./SplitButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/SplitButton.tsx`
- **Catalog row:** #124 SplitButton (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
CLOSED (not loading)
  → click primary button → props.onClick() [stays CLOSED]
  → click caret button → OPEN
  → disabled=true → both buttons inert

OPEN
  → click caret button → CLOSED
  → click option (enabled) → option.onClick() → CLOSED
  → click option (disabled) → no-op (stays OPEN)
  → click outside → CLOSED
  → Escape key → CLOSED

LOADING
  → click primary button → no-op
  → click caret button → no-op
  [both parts visually and functionally disabled]
```

---

## 2. Primary button behavior

Clicking the primary button fires `props.onClick()` directly. It does not open the dropdown. The primary button is independent of the dropdown state.

---

## 3. Caret button behavior

The caret button is a narrow icon-only button that toggles the dropdown. It does not trigger the primary action.

---

## 4. Click-outside detection

Document-level `mousedown` listener. When a click is detected outside the component ref, the dropdown closes.

---

## 5. Keyboard behavior

| Key | Context | Action |
|---|---|---|
| Enter / Space | Primary button focused | Fire `props.onClick()` |
| Enter / Space | Caret button focused | Toggle dropdown |
| Escape | Dropdown open | Close dropdown |
| ArrowDown | Dropdown open | Focus next option |
| ArrowUp | Dropdown open | Focus previous option |
| Enter | Option focused | Fire option.onClick() → close |
| Tab | Dropdown open | Close dropdown, advance document focus |

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SB1 | Low | Arrow key navigation in dropdown not implemented in M1 | Accepted-risk M1 |
| G-SB2 | Low | Loading state has no aria-live announcement | Accepted-risk M1 |
