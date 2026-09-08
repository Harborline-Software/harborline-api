# Slider — Interaction Contract

- **Component:** Slider
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Slider.Semantic.md) · [Accessibility](./Slider.Accessibility.md) · [Styling](./Slider.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Slider.tsx`
- **Catalog row:** #119 Slider (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Input handling

The native `<input type="range">` handles all pointer and keyboard events. `onChange` converts `e.target.value` to a number and calls `onValueChange(v)`. If uncontrolled, updates internal state.

---

## 2. State machine

```
value changes via drag or keyboard
  → onChange fires
  → if uncontrolled: setInternal(v)
  → onValueChange?.(v)
  → pct = (v - min) / (max - min) * 100
  → track fill + thumb position update
```

---

## 3. Keyboard (native `<input type="range">`)

| Key | Behaviour |
|---|---|
| `Arrow Right` / `Arrow Up` | Increment by `step` |
| `Arrow Left` / `Arrow Down` | Decrement by `step` |
| `Page Up` | Increment by large step (browser default: 10% of range) |
| `Page Down` | Decrement by large step |
| `Home` | Jump to `min` |
| `End` | Jump to `max` |

All keyboard behavior is native browser range input — no custom key handler.

---

## 4. Disabled

`disabled` is passed to the native input. Visual thumb and track are styled identically (no separate disabled style in M1 beyond native browser behavior).

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SL1 | Low | No explicit disabled styling on the custom track/thumb overlay in M1 | Accepted-risk M1; native input opacity provides sufficient visual cue |
| G-SL2 | Low | Vertical orientation layout uses `flex-col` wrapper but the native range input still renders horizontally in M1 (no CSS `-webkit-appearance: slider-vertical`) | Accepted-risk M1; vertical is a best-effort layout |
