# DateField — Styling Contract

- **Component:** DateField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DateField.Semantic.md) · [Interaction](./DateField.Interaction.md) · [Accessibility](./DateField.Accessibility.md)
- **Related contracts:** [TextField.Styling.md](./TextField.Styling.md) — DateField shares the `--sf-input-*` token surface; this contract documents only the deltas. [FormField.Styling.md](./FormField.Styling.md) — composing wrapper.
- **Reference implementation:** `packages/ui-react/src/components/forms/DateField.tsx`
- **Catalog row:** #37 DateInput (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

DateField is a thin wrapper around a native HTML `<input type="date">`. It
inherits the `--sf-input-*` token surface defined by
[TextField.Styling](./TextField.Styling.md) — same border, focus, disabled,
and error treatments. This contract names only the **deltas** specific to
DateField:

1. The browser-rendered native date picker (which is a non-themeable
   surface inside the input's chrome).
2. The single-size baseline (DateField does NOT expose the `size` axis in
   M1).
3. Three small browser-engine considerations (calendar-icon styling,
   placeholder absence, min/max enforcement visual).

The contract does NOT redefine the inherited token surface; consult
[TextField.Styling](./TextField.Styling.md) for the `--sf-input-*` family.

---

## 2. Token surface (deltas only)

DateField consumes the full `--sf-input-*` family from TextField. No new
component-local tokens are introduced in M1.

The fixed-size baseline corresponds to `md` from TextField's size axis
(`px-3 py-2 text-sm` → `--sf-input-padding-md` + `--sf-input-font-size-md`).
A future enhancement may expose the size axis on DateField; out of scope
for M1.

---

## 3. Tailwind class recipes (M1 default layer)

The shipping `DateField` recipe is **identical** to TextField's md-size
recipe with the same error / focus / disabled overlays:

```
w-full rounded-md border bg-white px-3 py-2 text-sm text-gray-900
focus:outline-none focus:ring-1

(error)    border-red-400 focus:border-red-500 focus:ring-red-500
(default)  border-gray-300 focus:border-blue-500 focus:ring-blue-500
(disabled) cursor-not-allowed bg-gray-50 opacity-60
```

The only structural difference from TextField is `type="date"`, which
triggers the browser-native date picker. There is no `placeholder` prop,
no `size` prop, no `aria-invalid` attribute (the `aria-invalid` IS emitted
from the implementation — see Accessibility §5).

---

## 4. Visual state inventory

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **default** | enabled, no error, no focus | border | `--sf-input-border` |
| **default focus-visible** | keyboard focus | border + ring | `--sf-input-border-focus` + `--sf-input-ring-focus` |
| **error** | `error === true`, no focus | border | `--sf-input-border-error` |
| **error focus-visible** | error + keyboard focus | border + ring | `--sf-input-border-focus-error` + `--sf-input-ring-focus-error` |
| **disabled** | `disabled === true` | full surface | `--sf-input-bg-disabled` + `--sf-input-opacity-disabled` |
| **value: empty** | `value === ''` | input chrome | browsers render a placeholder-like "mm/dd/yyyy" (locale-dependent); not themeable via token |
| **value: present** | `value` is a YYYY-MM-DD string | input chrome | the date displays in the user's locale format (browser-rendered) |
| **picker open** | browser-native picker is visible | picker chrome | NOT themeable in M1 (see §5) |

State precedence is the same as TextField (§"Visual state inventory").

---

## 5. Browser-native date picker — known non-themeable surface

The `<input type="date">` element renders a browser-native picker UI that is
**not themeable through CSS tokens** in M1. Different browsers render
substantially different pickers:

| Browser | Picker chrome |
|---|---|
| Chrome / Edge | Compact calendar dropdown with locale-aware month/day order |
| Safari | iOS/macOS-style spinner picker on mobile; calendar on desktop |
| Firefox | Compact calendar dropdown; minor visual differences |

The contract treats this as **out-of-scope**: the picker is a browser
implementation detail. Provider themes that need full visual consistency
(branded calendar, custom typography, RTL support) SHOULD swap to a
custom-component date picker — out of scope for M1. The native picker is
the M1 baseline.

**Council open question.** Should a future amendment introduce a
DatePicker component (separate from DateField) that wraps a JS calendar
library (`react-day-picker`, `react-datepicker`, etc.)? Tracked in
Semantic §7; **not in M1 scope.**

---

## 6. Calendar-icon styling — minor browser delta

Webkit-based browsers (Safari, Chrome) render a small calendar icon
(`::-webkit-calendar-picker-indicator`) on the right side of the input.
This icon is:

- Visible in M1 (no CSS suppression).
- Theming-limited (you can adjust opacity / cursor; you cannot change the
  glyph).
- Useful as an affordance — clicking it opens the picker.

Firefox does NOT render a calendar icon in the input chrome (Firefox
opens the picker on input click instead). This is an acceptable cross-
browser visual asymmetry for M1.

A future enhancement could overlay a custom icon (via a wrapper `<div>`
with an icon positioned over the input), but this complicates the layout
and is deferred.

---

## 7. Composition with FormField

Extends TextField — see [TextField.Styling §5](./TextField.Styling.md).
DateField's `id={name}` + `aria-describedby` from `FormFieldContext` work
the same way.

---

## 8. Open questions

1. **Size axis exposure.** DateField does NOT expose `size: 'sm' | 'md' |
   'lg'` in M1; it's hard-coded to md. Should a future amendment add the
   prop? Probably yes for consistency with TextField — track as a
   semantic-contract addition, not a styling change.
2. **Min/max visual treatment.** When the user types or picks a date
   outside `[min, max]`, the browser silently rejects the entry (the
   value doesn't update). No visual signal in M1. Should DateField add a
   visual "out of range" treatment? Probably yes; track as an
   enhancement.
3. **Custom DatePicker component.** Whether to build a non-native
   alternative for browser-consistency / RTL / branding reasons. Tracked
   in Semantic §7.

---

## 9. Do / Don't

### Do

- Treat DateField as a thin specialisation of TextField — same token
  surface, same recipes, same error/focus/disabled behaviour.
- Use ISO-8601 dates (`YYYY-MM-DD`) for the `value` and `min`/`max` props
  per the HTML spec.
- Honor `prefers-reduced-motion: reduce` on any future picker-transition
  animation (M1 has none; the native picker honors the OS setting
  directly).

### Don't

- Don't try to restyle the browser-native picker via CSS — pseudo-elements
  vary per browser and any "themed" picker built on `::-webkit-*` will
  break in Firefox.
- Don't use `placeholder` on DateField — the prop doesn't exist in M1
  (the browser-native "mm/dd/yyyy" placeholder is the affordance).
- Don't add an `aria-label` overriding the parent FormField's label. The
  FormField provides the programmatic label.

---

## 10. Parity notes

- **Blazor (future HarborlineDateField track):** consumes the same
  `--sf-input-*` family. Same browser-native picker limitations apply.
- **React (this contract):** as documented.
- **Web Components (Phase M4, Lit):** TBD; native `<input type="date">`
  in the shadow tree with attribute reflection.

---

## References

- [DateField.Semantic.md](./DateField.Semantic.md) — prop contract
- [DateField.Interaction.md](./DateField.Interaction.md) — behavioural contract
- [DateField.Accessibility.md](./DateField.Accessibility.md) — ARIA + describedby
- [TextField.Styling.md](./TextField.Styling.md) — parent token surface
- [FormField.Styling.md](./FormField.Styling.md) — composing wrapper
- `_shared/design/tokens-guidelines.md` — `--sf-*` token naming conventions
- `_shared/design/tokens/forms.tokens.json` — concrete default values
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
