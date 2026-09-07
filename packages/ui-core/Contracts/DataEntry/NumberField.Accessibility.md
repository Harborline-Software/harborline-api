# NumberField — Accessibility Contract

- **Component:** NumberField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumberField.Semantic.md) · [Interaction](./NumberField.Interaction.md) · [Styling](./NumberField.Styling.md)
- **Related contracts:** [TextField.Accessibility.md](./TextField.Accessibility.md) — NumberField inherits the same ARIA + describedby pattern; this contract documents the number-specific deltas. [FormField.Accessibility.md](./FormField.Accessibility.md) — describedby thread definition.
- **Reference implementation:** `packages/ui-react/src/components/forms/NumberField.tsx`
- **Catalog row:** #90 NumericTextBox — NumberField is the FormField-family controlled wrapper for numeric input; see MG-2 for full spec alignment (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

NumberField is a thin wrapper around a native HTML `<input type="number">`.
Native browser semantics provide most of its accessibility surface —
the implicit `spinbutton` role, the `valuemin` / `valuemax` / `valuenow`
ARIA pseudo-properties (via the `min`/`max`/`value` HTML attributes), and
the native numeric keyboard model. This contract names the **deltas** from
TextField.Accessibility:

1. The `spinbutton` role (vs `textbox` for TextField).
2. The `aria-valuemin` / `aria-valuemax` / `aria-valuenow` implicit
   mapping.
3. The step keyboard model (arrow keys increment / decrement).
4. The decimal / locale considerations.

For shared inherited behaviour (label-input linkage, `aria-describedby`
threading, `aria-invalid` on error, native disabled state), see
[TextField.Accessibility.md](./TextField.Accessibility.md).

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. ARIA structural role

NumberField renders a native `<input type="number">`. The implicit role
varies per browser / AT:

- WebKit + Blink (Safari, Chrome, Edge): announces as `spinbutton`.
- Firefox: announces as a numeric input with spinner.
- AT (NVDA, JAWS): hears "edit, spin button" or "number" depending on
  vendor.

Browsers automatically map the HTML `min` / `max` / `value` / `step`
attributes to ARIA's `aria-valuemin` / `aria-valuemax` / `aria-valuenow` /
`aria-valuestep` — the contract does NOT require explicit emission.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 3. Inherited behaviour

The following items inherit from TextField.Accessibility without change:

| Item | Inherited section |
|---|---|
| Label-input linkage via `id={name}` ↔ FormField's `htmlFor={name}` | [TextField.Accessibility §3](./TextField.Accessibility.md) |
| `aria-describedby` from `FormFieldContext` | [TextField.Accessibility §4](./TextField.Accessibility.md) |
| `aria-invalid={true}` when `error={true}` | [TextField.Accessibility §5](./TextField.Accessibility.md) |
| Native `disabled` semantics | [TextField.Accessibility §6](./TextField.Accessibility.md) |
| Focus management — controlled `value` re-renders don't lose focus | [TextField.Accessibility §8](./TextField.Accessibility.md) |
| Visible focus ring | [TextField.Accessibility §8](./TextField.Accessibility.md) |
| Color contrast minimums on input chrome | [TextField.Accessibility §9](./TextField.Accessibility.md) |

---

## 4. Keyboard navigation — number-specific deltas

NumberField inherits the native `<input type="number">` keyboard model:

| Key | Behaviour |
|---|---|
| Tab | Move focus into the input. |
| Shift+Tab | Move focus out backwards. |
| Number keys (0-9), period, comma, minus | Type into the field. (Comma vs period depends on the user's locale; browsers normalise on commit.) |
| Backspace / Delete | Edit text. |
| Arrow Left / Arrow Right | Move caret within the typed value. |
| Arrow Up | Increment by `step` (default `1`; pinned by the `step` attribute). |
| Arrow Down | Decrement by `step`. |
| Page Up / Page Down (some browsers) | Increment / decrement by a larger amount (typically 10×`step`). |
| Home / End | Move caret to start / end of typed value. |
| Enter | If inside a `<form>`, submit. |

The Arrow Up / Arrow Down increment behaviour is the **defining
characteristic** of the `spinbutton` role and the reason NumberField
uses `type="number"` rather than text-with-validation.

**WCAG citations:**
- WCAG 2.2 SC 2.1.1 Keyboard.
- WCAG 2.2 SC 2.1.2 No Keyboard Trap.

---

## 5. Step / min / max — programmatic exposure

When `step`, `min`, `max` are supplied as props, the implementation forwards
them to native HTML attributes:

```tsx
<input type="number" step={step} min={min} max={max} ... />
```

Browsers automatically map these to:

- `aria-valuemin` ← `min`
- `aria-valuemax` ← `max`
- `aria-valuenow` ← `value`
- `aria-valuestep` ← `step` (some browsers)

AT users hear the constraint on focus ("number, minimum 0, maximum 100").

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value — the constraint
is programmatically determined via native HTML semantics.

---

## 6. Decimal / locale considerations

The HTML spec defines `<input type="number">` value as a floating-point
decimal with `.` as the separator. The visible display may use `,` (comma)
as the decimal separator in non-English locales:

| Locale | Visible display | Underlying `value` |
|---|---|---|
| en-US | `3.14` | `"3.14"` |
| de-DE | `3,14` (some browsers) | `"3.14"` |
| ja-JP | `3.14` (same as en-US) | `"3.14"` |

AT announcement follows the visible display, which is locale-aware.

**Council open question.** Should NumberField provide automatic locale-
aware formatting (currency, percentage, grouping separators)? Current
contract: no — NumberField is a raw numeric input. A specialised
CurrencyField is a future enhancement.

**WCAG citation:** WCAG 2.2 SC 3.1.1 Language of Page.

---

## 7. Touch targets

| Size | Approximate height | WCAG 2.2 SC 2.5.8 status |
|---|---|---|
| M1 baseline (`md` only) | ~36px | meets the 24 × 24 minimum |

The browser-native spinner buttons on the right side (Chrome / Safari /
Edge) are smaller (~12-16px each); they are **supplementary** affordances
in addition to the keyboard step model. Mouse users have the spinner;
keyboard users have arrow keys. Touch users have the numeric on-screen
keyboard. All three paths meet WCAG.

**WCAG citation:** WCAG 2.2 SC 2.5.8 Target Size (Minimum).

---

## 8. Reduced motion

The browser-native spinner has no animation. NumberField itself has none.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 9. Known gaps

| # | Gap | Fix path |
|---|---|---|
| G1 | Min/max constraint not visually surfaced as a hint | Follow-on PR may auto-generate hint text into FormField's `hint` slot |
| G2 | No `inputmode="decimal"` or `pattern` forwarding | Future amendment may expose for fine control |
| G3 | No `autocomplete` (e.g., `"transaction-amount"`) | Future amendment may add `autocomplete?: string` prop (WCAG SC 1.3.5) |
| G4 | No locale-aware formatting (currency, grouping separators) | Build a separate CurrencyField; NumberField stays raw |

---

## 10. Do / Don't

### Do

- Use `step` to drive the increment unit — both keyboard (Arrow Up/Down)
  and spinner click use this value. AT announces it.
- Use `min` / `max` to constrain the valid range — browsers expose these
  as `aria-valuemin` / `aria-valuemax` automatically.
- Wrap in a FormField for the standard label / hint / error treatment.
- Treat the parent FormField's `hint` slot as the home for human-readable
  constraint description ("Enter a value between 0 and 100").

### Don't

- Don't use `type="text"` with manual numeric validation to avoid the
  spinner — that loses the `spinbutton` role, the numeric keyboard, and
  the native enforcement.
- Don't omit `aria-invalid={true}` when the parent FormField's `error` is
  set.
- Don't add `aria-valuetext` unless you have a specific reason to override
  the browser's automatic announcement (e.g., spelling-out a currency
  amount). Default automatic behaviour is correct for most cases.
- Don't right-align via inline `style={{ textAlign: 'right' }}` — the
  styling contract documents this as deferred to a future token.

---

## 11. Parity notes

- **Blazor (future HarborlineNumberField track):** consumes the same
  accessibility contract. Native `<input type="number">` element.
- **React (this contract):** as documented.
- **Web Components (Phase M4, Lit):** TBD; native `<input type="number">`
  in the shadow tree.

---

## References

- [NumberField.Semantic.md](./NumberField.Semantic.md) — prop contract
- [NumberField.Interaction.md](./NumberField.Interaction.md) — behavioural contract
- [NumberField.Styling.md](./NumberField.Styling.md) — token surface + visual states
- [TextField.Accessibility.md](./TextField.Accessibility.md) — inherited ARIA behaviour
- [FormField.Accessibility.md](./FormField.Accessibility.md) — describedby thread definition
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `spinbutton`, `aria-valuemin`, `aria-valuemax`, `aria-valuenow`, `aria-describedby`, `aria-invalid`
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 1.3.5 Identify Input Purpose
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.1.2 No Keyboard Trap
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
- WCAG 2.2 SC 3.1.1 Language of Page
- WCAG 2.2 SC 4.1.2 Name, Role, Value
