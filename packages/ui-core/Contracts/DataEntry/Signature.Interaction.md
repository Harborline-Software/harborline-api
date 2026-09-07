# Signature — Interaction Contract

- **Component:** Signature
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Signature.Semantic.md) · [Interaction](./Signature.Interaction.md) · [Accessibility](./Signature.Accessibility.md) · [Styling](./Signature.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Signature.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract covers mouse drawing, touch drawing, keyboard-native typed
capture, export, clear, and disabled mode.

---

## 2. Mouse drawing

| Event | Behaviour |
|---|---|
| `mousedown` | `startDraw` — captures first point, sets `drawing = true`. |
| `mousemove` | `moveDraw` — appends points to `currentPath.ref`, redraws (when `drawing === true`). |
| `mouseup` | `endDraw` — finalises path, appends to `paths`, calls `exportValue()`. |
| `mouseleave` | `endDraw` — same as mouseup (prevents stuck strokes when pointer leaves canvas). |

---

## 3. Touch drawing

| Event | Behaviour |
|---|---|
| `touchstart` | `startDraw` — captures first touch point. |
| `touchmove` | `moveDraw` — `e.preventDefault()` to suppress scroll, appends touch points. |
| `touchend` | `endDraw` — finalises path, exports. |

The canvas has `style={{ touchAction: 'none' }}` to prevent default touch
scrolling while drawing.

## 4. Keyboard-equivalent typed capture

`showTypedInput` defaults to `true`. The component renders a native text input
labelled "Your name (as signature)" after the canvas. Standard keyboard editing
applies; no custom key bindings or focus trap are introduced.

On each input change:

1. Existing freehand paths are cleared so one capture method remains active.
2. A non-empty name is rendered into the canvas preview.
3. `onChange` fires in the configured `format` (`svg` or `png`).
4. Emptying the input clears the preview and fires `onChange('')`.

Beginning a pointer stroke clears the typed input before drawing. This makes
the latest method authoritative and prevents typed and freehand marks from
being combined accidentally.

`showTypedInput={false}` is conformant only when the host supplies an
equivalent keyboard capture path and communicates it to assistive technology.
`ESignatureField` is the canonical host: its Type tab owns the keyboard path
and the draw instructions link the canvas to that tab.

---

## 5. Export — `exportValue()`

On each stroke end:
- `format === 'png'`: `onChange?.(canvas.toDataURL('image/png'))`.
- `format === 'svg'`: builds a minimal SVG string from all path data, calls `onChange?.(svgString)`.

Color `'currentColor'` resolves to `'#000'` in the canvas context.

---

## 6. Clear

The "Clear" button (positioned absolutely, top-right of canvas):
1. Resets `paths = []`, `currentPath.ref = []`, and the typed-name input.
2. Clears the canvas (`ctx.clearRect`).
3. Calls `onChange?.('')`.
4. Calls `onClear?.()`.

---

## 7. Disabled mode

When `disabled === true`:

- `startDraw` returns immediately (drawing is blocked).
- The typed-name input is disabled.
- Clear button has `disabled` attribute set — non-interactive.
- Canvas cursor: `cursor-not-allowed opacity-50`.

---

## 8. Canvas coordinate mapping

Points are mapped from client coordinates to canvas coordinates:
```
x = e.clientX - rect.left
y = e.clientY - rect.top
```

Where `rect` is `canvas.getBoundingClientRect()`. This is correct for
CSS-scaled canvases only if the canvas `width` attribute matches the rendered
pixel width. If the container is narrower than 400px, points will be offset.

---

## 9. Known gaps

| # | Gap | Impact |
|---|---|---|
| G1 | Canvas `width` defaults to 400 when CSS width is a percentage — coordinate mapping wrong at other widths | Strokes appear offset on narrow containers |
| G2 | `value` / `defaultValue` not consumed — canvas always starts blank | Cannot restore a saved signature |
| G3 | `exportScale` and `smooth` props are no-ops | Features users may expect from these props are absent |
| G4 | No undo / redo for individual strokes | Users must clear the entire signature to fix a mistake |
