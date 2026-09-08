# NumberField — Interaction Contract

- **Component:** NumberField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumberField.Semantic.md) · [Styling](./NumberField.Styling.md) · [Accessibility](./NumberField.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumberField.tsx`
- **Catalog row:** #90 NumericTextBox — NumberField is the FormField-family controlled wrapper for numeric input; see MG-2 for full spec alignment (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Scope

NumberField wraps a native `<input type="number">`. Most keyboard behaviour
(arrow-key step, spinner buttons) is browser-owned. This contract documents
the public surface, the typing model, and the disabled / error treatments.

Visual styling and ARIA wiring are owned by Styling and Accessibility (PAO).

---

## 2. Typing

- **Trigger:** any change event on the native input — typing, paste, native
  spinner click, arrow-key step.
- **Callback:** `onChange(e.target.value)` fires with the **raw string** (see
  Semantic §3.1). Per keystroke.
- **Intermediate states:** mid-typing strings like `""`, `"-"`, `"1."`,
  `"1e"`, `"."` flow through as-is. NumberField does not coerce or block
  them.
- **Paste:** the native input accepts pastes — content that doesn't parse as
  a number is rejected by the browser (paste silently drops) before
  `onChange` would fire. Host code does not see invalid pastes.

---

## 3. Spinner / arrow-key step

- **Trigger:**
  - Click the native up / down spinner buttons (browser-rendered).
  - Focus the input and press ArrowUp / ArrowDown.
  - Hover the input and scroll the mouse wheel (browser-dependent — some
    browsers step on wheel, others don't).
- **Step value:** the `step` prop (default browser-defined, typically `1`).
- **Bounds enforcement:** spinner / arrow-key step is clamped to
  `[min, max]` by the browser. Typed input is not clamped (Semantic §3.2).
- **Callback:** each step fires `onChange(newValueString)`.

---

## 4. Disabled mode

- **Trigger:** `disabled === true`.
- **Behaviour:**
  - Native `disabled` attribute on the input.
  - Spinner buttons are non-interactive.
  - Not focusable via Tab.
  - Visual: `cursor-not-allowed bg-gray-50 opacity-60`.

---

## 5. Error mode

- **Trigger:** `error === true`.
- **Behaviour:**
  - `aria-invalid={true}` on the input.
  - Red border + red focus ring.
  - Typing and spinner interactions remain enabled.
  - Error message comes from the parent FormField.

---

## 6. Keyboard behaviour

| Key | Behaviour |
| --- | --- |
| Digit keys, `.`, `-`, `e` | Insert character; `onChange` fires. |
| ArrowUp / ArrowDown | Step by `step`; clamped to `[min, max]`; `onChange` fires. |
| PageUp / PageDown (browser-dependent) | Larger step (typically 10×); `onChange` fires. |
| Backspace / Delete | Remove characters; `onChange` fires. |
| Tab / Shift+Tab | Move focus out. |
| Enter | Submit the enclosing form (browser default). |

---

## 7. Mouse-wheel scroll (browser-default sharp edge)

- Some browsers step the value on mouse-wheel scroll **only when the input
  is focused**. This is a long-standing UX papercut — accidental wheel
  scrolling while the field is focused changes the number.
- NumberField does **not** suppress wheel-scroll in M1. Hosts that need to
  suppress it can attach a `wheel` event handler via a ref or use
  `inputMode="decimal"` on a TextField as a workaround (the latter loses
  the spinner).
- This may be addressed in a future revision (a `suppressWheelScroll?:
  boolean` prop or a default-off-on-focus policy).

---

## 8. Interaction-state precedence

1. **Disabled** — input non-interactive; spinners disabled; not in tab
   order.
2. **Error** — interactive; error visual treatment + `aria-invalid`.
3. **Idle** — normal interactive state.

---

## 9. Council open questions (Interaction)

1. **Mouse-wheel scroll.** Suppress by default, or keep browser-native?
   (Leaning: add an opt-out prop in a later wave; do not change the
   default for M1.)
2. **Mobile keyboard.** `inputMode` defaults to numeric for
   `<input type="number">` on most mobile browsers, but `inputMode="decimal"`
   gives a better experience for decimal entry. Should NumberField always
   set `inputMode="decimal"` when `step` is fractional? (Leaning: yes —
   tighten the implementation; add to a fast-follow.)
3. **`onBlur` commit.** Same question as TextField — should NumberField
   add `onBlur` so hosts can validate / parse on commit?
