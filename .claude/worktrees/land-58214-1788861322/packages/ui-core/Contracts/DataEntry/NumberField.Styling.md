# NumberField — Styling Contract

- **Component:** NumberField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumberField.Semantic.md) · [Interaction](./NumberField.Interaction.md) · [Accessibility](./NumberField.Accessibility.md)
- **Related contracts:** [TextField.Styling.md](./TextField.Styling.md) — NumberField shares the `--sf-input-*` token surface; this contract documents only the deltas. [FormField.Styling.md](./FormField.Styling.md) — composing wrapper.
- **Reference implementation:** `packages/ui-react/src/components/forms/NumberField.tsx`
- **Catalog row:** #90 NumericTextBox — NumberField is the FormField-family controlled wrapper for numeric input; see MG-2 for full spec alignment (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

NumberField is a thin wrapper around a native HTML `<input type="number">`.
It inherits the `--sf-input-*` token surface defined by
[TextField.Styling](./TextField.Styling.md) — same border, focus, disabled,
and error treatments. This contract names only the **deltas** specific to
NumberField:

1. The browser-rendered increment/decrement spinner buttons (which appear
   inside the input's chrome and vary per browser).
2. The text alignment consideration (numeric content typically right-
   aligns in financial/accounting contexts; the M1 default is left-align
   matching TextField).
3. The `step` / `min` / `max` constraint visualisation.

The contract does NOT redefine the inherited token surface; consult
[TextField.Styling](./TextField.Styling.md) for the `--sf-input-*` family.

---

## 2. Token surface (deltas only)

NumberField consumes the full `--sf-input-*` family from TextField. No new
component-local tokens are introduced in M1.

The fixed-size baseline corresponds to `md` from TextField's size axis
(`px-3 py-2 text-sm`). A future enhancement may expose the size axis on
NumberField; out of scope for M1.

### 2.1 Possible future addition — text-align token

Financial / accounting contexts often right-align currency / quantity
values. A future amendment may introduce:

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-input-text-align` | text-align value (`left` \| `right` \| `center`) | NumberField default would shift to `right`; TextField default stays `left` |

This is **out of scope for M1**. The shipping implementation left-aligns
matching TextField; hosts that need right-alignment apply a custom class
externally.

---

## 3. Tailwind class recipes (M1 default layer)

The shipping `NumberField` recipe is **identical** to TextField's md-size
recipe with the same error / focus / disabled overlays:

```
w-full rounded-md border bg-white px-3 py-2 text-sm text-gray-900
focus:outline-none focus:ring-1

(error)    border-red-400 focus:border-red-500 focus:ring-red-500
(default)  border-gray-300 focus:border-blue-500 focus:ring-blue-500
(disabled) cursor-not-allowed bg-gray-50 opacity-60
```

The only structural difference from TextField is `type="number"`, which
triggers the browser-native numeric spinner buttons (on most browsers).
There is no `placeholder` prop and no `size` prop in M1.

---

## 4. Visual state inventory

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **default** | enabled, no error, no focus | border | `--sf-input-border` |
| **default focus-visible** | keyboard focus | border + ring | `--sf-input-border-focus` + `--sf-input-ring-focus` |
| **error** | `error === true`, no focus | border | `--sf-input-border-error` |
| **error focus-visible** | error + keyboard focus | border + ring | `--sf-input-border-focus-error` + `--sf-input-ring-focus-error` |
| **disabled** | `disabled === true` | full surface | `--sf-input-bg-disabled` + `--sf-input-opacity-disabled` |
| **value: empty** | `value === ''` or `value === undefined` | input content | empty input chrome |
| **value: present** | numeric value | input content | rendered as number; locale-formatted by the browser |
| **spinner buttons visible** | browser-native (no toggle) | input chrome on right | NOT themeable in M1 (see §5) |

State precedence is the same as TextField (§"Visual state inventory").

---

## 5. Browser-native numeric spinner — known non-themeable surface

The `<input type="number">` element renders browser-native spinner buttons
(small up/down chevrons) in the input's right chrome on most browsers:

| Browser | Spinner |
|---|---|
| Chrome / Edge | Inline up/down arrows; visible on hover/focus |
| Safari | Inline up/down arrows; visible on hover/focus |
| Firefox | Inline up/down arrows; styling-limited |
| Mobile browsers | Numeric on-screen keyboard; no spinner |

The spinner is **not themeable through `--sf-input-*` tokens** in M1.
Provider themes that need spinner consistency would build a custom
NumberInput component with explicit increment/decrement buttons —
out of scope for M1.

### 5.1 Suppressing the spinner (host opt-out)

Some hosts prefer no spinner (e.g., when the field is a currency input where
incrementing by 1 isn't useful). Suppressing the spinner is a host-side
CSS choice, not a NumberField contract concern:

```css
/* host CSS, not NumberField's */
input[type="number"]::-webkit-outer-spin-button,
input[type="number"]::-webkit-inner-spin-button { -webkit-appearance: none; }
input[type="number"] { -moz-appearance: textfield; }
```

The contract treats spinner suppression as host opt-in. A future amendment
may add a `hideSpinner?: boolean` prop.

---

## 6. Step / min / max — visual considerations

When `step`, `min`, and/or `max` are supplied:

- The browser enforces the constraint silently — typing or spinner-clicking
  outside `[min, max]` clamps the value to the nearest allowed; non-
  step-aligned values (`step=0.01`, user types `1.234`) are typically
  rejected on commit (varies by browser).
- The browser SHOULD expose the constraint to AT (e.g., "spinbutton, min
  0, max 100, step 1"), but support varies.
- NumberField does NOT visually surface the constraint in M1. **Gap G1**
  (§"Known gaps" in Accessibility) — a follow-on PR may surface as an
  automatic hint in the parent FormField.

---

## 7. Composition with FormField

Extends TextField — see [TextField.Styling §5](./TextField.Styling.md).
NumberField's `id={name}` + `aria-describedby` from `FormFieldContext` work
the same way.

---

## 8. Open questions

1. **Text-align token.** Should `--sf-input-text-align` (or a NumberField-
   specific `--sf-input-text-align-number`) be added so financial /
   accounting contexts can right-align? Current contract: deferred. Hosts
   right-align via a custom class.
2. **`hideSpinner` prop.** Should the contract expose a built-in way to
   suppress the browser-native spinner? Current contract: deferred to host
   CSS.
3. **Currency / percentage / unit formatting.** NumberField is a raw
   numeric input. A specialised CurrencyField (with currency-symbol prefix
   + grouping separator + locale-aware decimal) is a future enhancement,
   not part of NumberField.
4. **Size axis exposure.** Same as DateField — hard-coded to `md` in M1;
   future amendment may add `sm` / `lg`.

---

## 9. Do / Don't

### Do

- Treat NumberField as a thin specialisation of TextField — same token
  surface, same recipes, same error/focus/disabled behaviour.
- Use the `step` attribute to drive AT announcement of the valid increments.
- Pair `min` / `max` with the parent FormField's `hint` slot for explicit
  constraint communication.
- Honor `prefers-reduced-motion: reduce` on any future spinner-animation
  (M1 has none; the native spinner is browser-managed).

### Don't

- Don't try to restyle the browser-native spinner via CSS pseudo-elements
  without testing across browsers — vendor prefixes vary.
- Don't use `type="text"` with manual numeric validation to avoid the
  spinner — that loses the `spinbutton` AT role, the numeric keyboard on
  mobile, and the native step/min/max enforcement. If spinner suppression
  is required, use the host-CSS pattern in §5.1.
- Don't right-align without considering RTL layouts — the natural right-
  alignment expectation in LTR may flip in RTL.

---

## 10. Parity notes

- **Blazor (future HarborlineNumberField track):** consumes the same
  `--sf-input-*` family. Same browser-native spinner considerations apply.
- **React (this contract):** as documented.
- **Web Components (Phase M4, Lit):** TBD; native `<input type="number">`
  in the shadow tree.

---

## References

- [NumberField.Semantic.md](./NumberField.Semantic.md) — prop contract
- [NumberField.Interaction.md](./NumberField.Interaction.md) — behavioural contract
- [NumberField.Accessibility.md](./NumberField.Accessibility.md) — ARIA + describedby
- [TextField.Styling.md](./TextField.Styling.md) — parent token surface
- [FormField.Styling.md](./FormField.Styling.md) — composing wrapper
- `_shared/design/tokens-guidelines.md` — `--sf-*` token naming conventions
- `_shared/design/tokens/forms.tokens.json` — concrete default values
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
