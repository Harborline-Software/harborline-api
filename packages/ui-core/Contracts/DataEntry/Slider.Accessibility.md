# Slider — Accessibility Contract

- **Component:** Slider
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Slider.Semantic.md) · [Interaction](./Slider.Interaction.md) · [Styling](./Slider.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Slider.tsx`
- **Catalog row:** #119 Slider (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `type="range"` | Native `<input>` | Implicit `role="slider"` |
| `min` | Native `<input>` | Minimum value |
| `max` | Native `<input>` | Maximum value |
| `step` | Native `<input>` | Step increment |
| `value` | Native `<input>` | Current value |
| `disabled` | Native `<input>` | Boolean when disabled |

The native `<input type="range">` provides `role="slider"`, `aria-valuemin`, `aria-valuemax`, `aria-valuenow` automatically.

---

## 2. Labeling

The Slider has no built-in label. The host must associate a label via:
```tsx
<label htmlFor="volume-slider">Volume</label>
<Slider id="volume-slider" ... />  {/* id not exposed in M1 — Gap G-SL3 */}
```

In M1, the `id` prop is not exposed. Label association requires wrapping the native input inside a `<label>` or using host-level `aria-label`.

---

## 3. AT announcement

AT announces: `"[label if linked], [current value] of [max], slider"`. Arrow keys increment/decrement and AT announces the new value.

---

## 4. Visual vs AT overlap

The custom thumb and track are `pointer-events-none` decorative elements. AT interacts only with the native `<input>` (opacity-0 but in the DOM). This is the correct pattern — visual and AT layers are properly decoupled.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SL3 | Medium | `id` prop not exposed — host cannot link a `<label htmlFor>` to the native input | Accepted-risk M1; host can wrap in `<label>` |
| G-SL4 | Low | No `aria-label` prop on the native input — icon-only contexts have no accessible name | Accepted-risk M1; host must wrap in label |
