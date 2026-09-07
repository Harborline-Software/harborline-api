# ToggleGroup — Interaction Contract

- **Component:** ToggleGroup
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ToggleGroup.Semantic.md) · [Accessibility](./ToggleGroup.Accessibility.md) · [Styling](./ToggleGroup.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ToggleGroup.tsx`
- **Catalog row:** #A14 ToggleGroup (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine (single)

```
[A selected, B not, C not]
  → click A → onValueChange("A") — A remains selected (no deselect)
  → click B → onValueChange("B") — B selected
  → click C → onValueChange("C") — C selected
```

Single mode always has exactly one item selected (enforced by parent holding state).

---

## 2. State machine (multiple)

```
[A selected, B not]
  → click A → onValueChange([]) — A deselected
  → click B → onValueChange(["A","B"]) — B added
  → click A again → onValueChange(["B"]) — A removed
```

Multiple mode allows 0..n items selected simultaneously.

---

## 3. Disabled

Group-level `disabled` disables all buttons. Per-option `disabled` disables that button only. Disabled buttons do not respond to click or keyboard activation.

---

## 4. Keyboard

| Key | Behaviour |
|---|---|
| `Tab` | Moves focus across buttons in natural order |
| `Enter` / `Space` | Activates focused button (native `<button>` behavior) |

*Note: no arrow-key navigation between group buttons in M1. Each button is independently focusable via Tab.*

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TG1 | Low | Single mode does not deselect on re-click of active item — differs from Radix TogglePrimitive behavior | Accepted-risk M1; by design for keyboard/segmented-control pattern where at least one option must remain active |
| G-TG2 | Low | No arrow-key roving tabindex for the group — each button takes a separate Tab stop | Accepted-risk M1; ARIA radiogroup pattern would be more appropriate for single mode |
