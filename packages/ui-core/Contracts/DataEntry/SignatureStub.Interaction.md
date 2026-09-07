# SignatureStub — Interaction Contract

- **Component:** SignatureStub
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Draft
- **Companion contracts:** [Semantic](./SignatureStub.Semantic.md) · [Styling](./SignatureStub.Styling.md) · [Accessibility](./SignatureStub.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SignatureStub.tsx`

---

## 1. Scope

Governs the drawing lifecycle (pointer + touch), keyboard-native typed capture,
the Clear action, the controlled `value` round-trip, and disabled/error
presentation.

## 2. Activation & state

- **Begin** — `mousedown` / `touchstart` starts a stroke (unless `disabled`):
  the path is moved to the pointer position; stroke style/width/caps are set.
- **Draw** — `mousemove` / `touchmove` extends the path and strokes it; the
  internal `isEmpty` flag flips to false.
- **Commit** — `mouseup` / `mouseleave` / `touchend` ends the stroke and fires
  `onChange(canvas.toDataURL('image/png'))`.
- **Round-trip** — when `value` changes, the canvas is cleared and the supplied
  data-URL image is drawn back in; `isEmpty` is set from whether `value` is
  present.
- **Clear** — the **Clear signature** button wipes the canvas, sets `isEmpty`,
  and fires `onChange(null)`.

`isEmpty` drives two affordances: the centered "Sign here" placeholder (shown
only when empty) and the Clear button (shown only when non-empty and enabled).
Touch handlers call `preventDefault()` so drawing does not scroll the page
(`touch-action: none`).

## 3. Keyboard behaviour

| Key | Behaviour |
| --- | --- |
| Tab / Shift+Tab | Moves through the typed-name input and the **Clear signature** button when present. |
| Character/editing keys | Edit the native typed-name input using platform conventions. |
| Enter / Space | Activates the focused Clear button. No custom action is attached to the text input. |

`showTypedInput` defaults to `true`. On each typed-name change, the component
clears any pointer drawing, renders the name into the canvas preview, and emits
the PNG data URL through the existing `onChange` callback. Emptying the input
clears the preview and emits `null`. Beginning a pointer stroke clears the
typed input so the most recent capture method is authoritative.

`showTypedInput={false}` is conformant only when the host supplies and
documents an equivalent keyboard capture path.

## 4. Disabled handling

When `disabled`: `mousedown` / `touchstart` early-return (no drawing), the
typed-name input is disabled, the surface dims (`opacity-50`, muted
background), and the Clear button is hidden. Existing `value` still renders.

## 5. Interaction-state precedence

1. **Disabled** — no drawing, dimmed, Clear hidden (wins over all).
2. **Drawing active** — a stroke is in progress.
3. **Error** — destructive border + `role="alert"` message (independent of the
   above; can co-present with a drawn or empty canvas).
4. **Empty / normal** — placeholder shown when empty; Clear shown when non-empty.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
