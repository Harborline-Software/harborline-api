# ColorGradient — Interaction Contract

- **Component:** ColorGradient
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColorGradient.Semantic.md) · [Accessibility](./ColorGradient.Accessibility.md) · [Styling](./ColorGradient.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ColorGradient.tsx`
- **Catalog row:** #29 ColorGradient (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Gradient canvas click

`onClick` on canvas: computes `s = (x / width)`, `v = 1 - (y / height)`, clamped to [0, 1]. Calls `commit(hue, s, v)`.

`commit(h, s, v)`: converts to hex, updates HSV state, emits `onValueChange(hex)`.

---

## 2. Hue slider

`<input type="range" min=0 max=360>` — native range. `onChange`: calls `commit(Number(value), sat, bri)`.

---

## 3. Opacity slider

`<input type="range" min=0 max=100>` — updates `alpha` state only. Alpha is NOT included in the emitted hex color in M1.

---

## 4. Hex text input

`onChange`: updates raw value, attempts `hexToHsv`, silently ignores invalid hex. Emits `onValueChange(rawText)` even for invalid hex strings.

---

## 5. Canvas — drag not supported

Only click events on the canvas in M1. No mouse-drag to continuously change sat/bri. Gap G-CG1.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CG1 | Medium | Canvas only responds to click, not drag — user must click-to-select rather than drag | Accepted-risk M1 |
| G-CG2 | Medium | Alpha value is not emitted in the hex output — opacity slider has no effect on `onValueChange` | Accepted-risk M1; alpha deferred |
