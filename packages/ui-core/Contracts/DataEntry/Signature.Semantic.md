# Signature — Semantic Contract

- **Component:** Signature
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Signature.Interaction.md) · [Accessibility](./Signature.Accessibility.md) · [Styling](./Signature.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Signature.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled canvas signature pad

---

## 1. Purpose

Signature captures a signature through two equivalent paths: freehand canvas
drawing for pointer users and a typed-name input for keyboard and assistive-
technology users. Both paths export the same `string` payload (PNG data URL or
inline SVG) through `onChange`. A "Clear" button resets both paths.

---

## 2. Data model

Signature is semi-controlled: `value` and `defaultValue` are declared but the
component renders by maintaining an internal `paths` array. The canvas is the
source of truth; `onChange` fires after each stroke.

```typescript
interface SignatureProps {
  value?: string
  defaultValue?: string
  onChange?: (value: string) => void
  format?: 'svg' | 'png'
  exportScale?: number
  smooth?: boolean
  strokeWidth?: number
  color?: string
  backgroundColor?: string
  width?: string | number
  height?: string | number
  disabled?: boolean
  showTypedInput?: boolean
  className?: string
  onClear?: () => void
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `string` | — | Declared but NOT consumed for initial render — canvas always starts blank in M1. Known gap. |
| `defaultValue` | `string` | — | Declared but NOT consumed. Same gap. |
| `onChange` | `(value: string) => void` | — | Called after each stroke completes and after clear. On clear, called with `''`. |
| `format` | `'svg' \| 'png'` | `'svg'` | Export format. `'png'` uses `canvas.toDataURL('image/png')`; `'svg'` builds a minimal SVG string from the path data. |
| `exportScale` | `number` | — | Declared but NOT used in the implementation. |
| `smooth` | `boolean` | — | Declared but NOT used in the implementation. |
| `strokeWidth` | `number` | `2` | Canvas `lineWidth`. |
| `color` | `string` | `'currentColor'` | Stroke color. `'currentColor'` resolves to `'#000'` in the canvas context. |
| `backgroundColor` | `string` | `'transparent'` | Canvas fill. `'transparent'` means no fill is applied. |
| `width` | `string \| number` | `'100%'` | CSS width of the container. |
| `height` | `string \| number` | `160` | Canvas height in pixels. |
| `disabled` | `boolean` | `false` | When `true`, drawing is prevented and the canvas shows disabled styling. |
| `showTypedInput` | `boolean` | `true` | Renders the keyboard-native typed-name capture path. May be `false` only when the host provides and documents an equivalent keyboard path, as `ESignatureField` does with its Type method. |
| `className` | `string` | — | Additional classes on the root wrapper. |
| `onClear` | `() => void` | — | Optional additional callback when the Clear button is clicked. |

### 3.1 Canvas dimensions

The canvas element's `width` attribute is `typeof width === 'number' ? width : 400`
(defaults to 400 when width is a percentage string). The CSS width is `'100%'`.
This can cause drawing to appear squished or stretched when the container is
not 400px wide.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `string` | After each stroke completes (mouseup / touchend / mouseleave). Payload is the full export (SVG string or PNG data URL). |
| `onChange` | `string` | On each typed-name input change. The typed name is rendered into the selected SVG/PNG format; an empty input emits `''`. |
| `onChange` | `''` | After clear button is clicked. |
| `onClear` | — | After clear button is clicked. |

---

## 5. Variants and states

| State | Trigger |
|---|---|
| **Idle** | No active drawing |
| **Drawing** | `drawing === true` (mousedown / touchstart active) |
| **Typing** | Typed-name input contains a value; typing replaces freehand paths |
| **Disabled** | `disabled === true` |

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SIG1 | High | `value` is declared in the interface and prop table but NOT consumed — canvas always starts blank regardless of prop value. Controlled hydration story is broken. | Fix-deferred M2; prop should either be consumed or removed from interface |
| G-SIG2 | High | `defaultValue` is declared but NOT consumed. Same as G-SIG1. | Fix-deferred M2; pair with G-SIG1 |
| G-SIG3 | Medium | `exportScale` is declared but NOT used in implementation — export resolution is hardcoded. | Fix-deferred M2 |
| G-SIG4 | Medium | `smooth` is declared but NOT used — stroke smoothing algorithm is fixed. | Fix-deferred M2 |
| G-SIG5 | Low | Undo/redo (stroke history) not implemented | Accepted-risk M1 |
| G-SIG6 | Low | Canvas logical width hardcoded at 400px when CSS width is percentage | Accepted-risk M1 |
