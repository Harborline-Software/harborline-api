# ColorGradient — Styling Contract

- **Component:** ColorGradient
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColorGradient.Semantic.md) · [Interaction](./ColorGradient.Interaction.md) · [Accessibility](./ColorGradient.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ColorGradient.tsx`
- **Catalog row:** #29 ColorGradient (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`flex flex-col gap-2 w-48` (192px fixed width)
Disabled: `opacity-50 pointer-events-none`

---

## 2. Gradient canvas

`relative h-32 rounded cursor-crosshair select-none`
Background: `linear-gradient(to right, white, {pureHueColor})`

Overlay (black fade): `absolute inset-0 rounded` + `background: linear-gradient(to bottom, transparent, black)`

---

## 3. SV cursor indicator

`absolute w-3 h-3 rounded-full border-2 border-white shadow -translate-x-1/2 -translate-y-1/2`
Position: inline `left: ${sat*100}%`, `top: ${(1-bri)*100}%`
Background: inline `background: current` (shows current selected color)

---

## 4. Hue slider

`<input type="range">` with `accent-primary` class
Background: `linear-gradient(to right, #f00, #ff0, #0f0, #0ff, #00f, #f0f, #f00)` (rainbow hue strip)

---

## 5. Opacity slider

Standard `<input type="range">` with no special styling. `w-full`

---

## 6. Hex input

`w-full text-xs border border-input rounded px-2 py-1 font-mono`
