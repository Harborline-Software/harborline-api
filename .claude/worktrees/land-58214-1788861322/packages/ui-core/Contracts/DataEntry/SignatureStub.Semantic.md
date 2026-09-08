# SignatureStub — Semantic Contract

- **Component:** SignatureStub
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Draft
- **Companion contracts:** [Interaction](./SignatureStub.Interaction.md) · [Styling](./SignatureStub.Styling.md) · [Accessibility](./SignatureStub.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SignatureStub.tsx`

---

## 1. Purpose

`SignatureStub` is a lightweight, self-contained signature-capture field. It
renders an HTML5 `<canvas>` the user draws on with mouse or touch plus a native
typed-name input as the keyboard-equivalent capture path. Both methods emit the
captured signature as a PNG **data URL** through `onChange`. It supports an
optional label (with a required marker), an error message, a disabled state, a
configurable canvas size and pen colour, and an in-built **Clear** control.

"Stub" denotes that it is a dependency-free capture surface (no external
signature-pad library). The value round-trips: when a `value` data URL is
supplied it is re-drawn onto the canvas, so the field can be controlled and
re-hydrated from persisted state.

## 2. Data model

```typescript
export interface SignatureStubProps {
  value?: string                              // PNG data URL; redrawn onto the canvas when set
  onChange?: (dataUrl: string | null) => void // PNG data URL on stroke end; null on clear
  label?: string
  disabled?: boolean                          // default false
  width?: number                              // canvas px, default 400
  height?: number                             // canvas px, default 120
  penColor?: string                           // default '#1e293b'
  required?: boolean
  error?: string
  showTypedInput?: boolean                   // default true
  className?: string
}
```

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `value` | `string` | — | Controlled PNG data URL; re-drawn onto the canvas on change. Empty/undefined ⇒ shows the "Sign here" placeholder. |
| `onChange` | `(dataUrl: string \| null) => void` | — | Fired with the canvas PNG data URL on stroke end; fired with `null` when cleared. |
| `label` | `string` | — | Field label; also the canvas `aria-label` (falls back to `"Signature pad"`). |
| `disabled` | `boolean` | `false` | Blocks drawing, dims the surface, hides the Clear control. |
| `width` | `number` | `400` | Canvas intrinsic width (px); also caps the rendered `max-width`. |
| `height` | `number` | `120` | Canvas intrinsic height (px). |
| `penColor` | `string` | `'#1e293b'` | Stroke colour. |
| `required` | `boolean` | — | Renders the `*` required marker beside the label. |
| `error` | `string` | — | Error message rendered with `role="alert"`; switches the border to destructive. |
| `showTypedInput` | `boolean` | `true` | Renders the keyboard-native typed-name capture path. May be hidden only when a host provides and documents an equivalent keyboard path. |
| `className` | `string` | — | Applied to the outer wrapper. |

## 4. Events

| Event | Payload | When |
| --- | --- | --- |
| `onChange` | `string` (PNG data URL) | On stroke end (`mouseup` / `mouseleave` / `touchend`) after drawing. |
| `onChange` | `string` (PNG data URL) | On each non-empty typed-name change after the text is rendered into the canvas. |
| `onChange` | `null` | When the typed-name input is emptied. |
| `onChange` | `null` | When the **Clear signature** control is pressed. |

## 5. Slots

None — `SignatureStub` is a single self-contained field. The label, canvas,
typed-name input, placeholder, Clear button, baseline guide, and error text are
all rendered internally; there are no `children` or named slots.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
