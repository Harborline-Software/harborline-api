# FormField — Styling Contract

- **Component:** FormField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FormField.Semantic.md) · [Interaction](./FormField.Interaction.md) · [Accessibility](./FormField.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormField.tsx` + `FormFieldContext.tsx`
- **Catalog rows:** #55 FieldWrapper / #63 Form (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

FormField is the canonical field-wrapper: a label row + slotted input + hint /
error treatment. The visual surface has four regions (label, required-marker,
slot, message line) and three error-axis states (default, error, disabled —
the disabled axis is on the slotted child, not on FormField itself).

This contract names the `--sf-field-*` token surface that FormField uses,
distinct from the input-level tokens (`--sf-input-*`, owned by TextField /
DateField / NumberField / SelectField / CheckboxField). The split matters
because a single FormField wraps any child input — the wrapper's tokens
must compose cleanly with any input's tokens.

---

## 2. Token surface

FormField exposes the following CSS custom properties (the `--sf-field-*`
family). Adapters MUST consume these tokens; adapters MUST NOT hard-code
values in component code.

### 2.1 Label row

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-field-label-fg` | Foreground colour for the label text | none | Pairs with the host surface background at ≥ 4.5:1 |
| `--sf-field-label-font-size` | Label font size | none | Typically `0.875rem` (= `text-sm`) |
| `--sf-field-label-font-weight` | Label font weight | none | Typically `500` (= `font-medium`); slightly heavier than body to anchor the field |
| `--sf-field-required-marker-fg` | Foreground colour for the required-asterisk `*` | none | Typically a brand-error red (e.g., `--sf-color-error-500`); pairs with host surface at ≥ 3:1 (non-text per WCAG 2.2 SC 1.4.11 — the marker is a visual signal, not announced) |
| `--sf-field-required-marker-gap` | Spacing between label text and asterisk | none | Typically `0.125rem` (= `ml-0.5`) |

### 2.2 Message line (hint OR error)

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-field-hint-fg` | Hint text colour | hint mode | Pairs with host surface at ≥ 4.5:1; intentionally lower-contrast than label (hint is supplementary, not primary) |
| `--sf-field-hint-font-size` | Hint font size | none | Typically `0.75rem` (= `text-xs`) |
| `--sf-field-error-fg` | Error text colour | error mode | Pairs with host surface at ≥ 4.5:1; brand-error red (e.g., `--sf-color-error-600`) |
| `--sf-field-error-font-size` | Error font size | none | Same as hint (`text-xs`) so the message line height is stable across modes |

### 2.3 Layout

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-field-gap-rows` | Vertical gap between label row, slot, and message line | none | Typically `0.375rem` (= `gap-1.5`) |

### 2.4 Token additions to `forms.tokens.json`

The `--sf-field-*` family lives in a new token file
`_shared/design/tokens/forms.tokens.json`, separate from
`data-display.tokens.json`. This file is the shared surface for ALL Batch B
field primitives (FormField, TextField, SelectField, DateField, NumberField,
CheckboxField).

Recommended initial contents (FormField section only — other field tokens
land in the other field-primitive Styling contracts):

```jsonc
{
  "$schema": "https://design-tokens.github.io/community-group/format/",
  "_meta": {
    "name": "forms.tokens",
    "description": "Default design-system values for the DataEntry (Forms) component family. Components consume these via CSS custom properties (--sf-field-*, --sf-input-*, --sf-checkbox-*).",
    "adr": "0017-A1",
    "status": "Draft",
    "version": "0.1.0"
  },
  "field": {
    "label": {
      "fg": "#374151",
      "fontSize": "0.875rem",
      "fontWeight": "500"
    },
    "requiredMarker": {
      "fg": "#EF4444",
      "gap": "0.125rem"
    },
    "hint": {
      "fg": "#6B7280",
      "fontSize": "0.75rem"
    },
    "error": {
      "fg": "#DC2626",
      "fontSize": "0.75rem"
    },
    "layout": {
      "gapRows": "0.375rem"
    },
    "_notes": {
      "contrast": "label.fg, hint.fg, error.fg verified ≥ 4.5:1 on host surface (typically white #FFFFFF). requiredMarker.fg on host surface verified ≥ 3:1 (WCAG SC 1.4.11 non-text).",
      "hint-vs-error-precedence": "When both hint and error are supplied, the implementation hides hint and shows error. The token surface treats them as independent slots; rendering order is owned by the interaction contract."
    }
  }
}
```

Other field-primitive sub-namespaces (`input`, `select`, `checkbox`, etc.)
land in their respective contracts and PRs.

---

## 3. Tailwind class recipes (M1 default layer)

| Region | Tailwind recipe (M1 default) | Token resolution |
|---|---|---|
| Root container | `flex flex-col gap-1.5` | `--sf-field-gap-rows` |
| Label | `text-sm font-medium text-gray-700` (`Label.Root` from Radix UI) | `--sf-field-label-fg` + `--sf-field-label-font-size` + `--sf-field-label-font-weight` |
| Required marker | `ml-0.5 text-red-500` + `aria-hidden="true"` | `--sf-field-required-marker-gap` + `--sf-field-required-marker-fg` |
| Slot wrapper | `<div>` (no classes) | layout neutral |
| Hint text | `text-xs text-gray-500` (rendered only when `hint && !error`) | `--sf-field-hint-fg` + `--sf-field-hint-font-size` |
| Error text | `text-xs text-red-600` + `role="alert"` (rendered only when `error`) | `--sf-field-error-fg` + `--sf-field-error-font-size` |

---

## 4. Visual state inventory

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **default** | `error` absent, `hint` absent | message line hidden; label row + slot only | base tokens |
| **hint** | `hint` supplied, `error` absent | hint message rendered below slot | `--sf-field-hint-*` |
| **error** | `error` supplied | error message rendered (replaces hint if both supplied) | `--sf-field-error-*` |
| **required** | `required === true` | red asterisk after label text | `--sf-field-required-marker-*` |
| **slot in error treatment** | `error` supplied — the child input also renders its error visual (red border + red focus ring) | slot child | child's own `--sf-input-*-error` tokens |

**Mode precedence (highest first):** error > hint > default. The two message
modes are mutually exclusive; the implementation enforces this in JSX (`hint
&& !error` for hint render).

---

## 5. Slot-composition contract

FormField is a wrapper, and most of its styling work is to render the label /
hint / error visuals so child inputs can stay focused on their own concerns.
The slot-composition expectations are:

- Child inputs SHOULD render their own error-axis treatment (red border, red
  focus ring) when the host passes `error={true}` to the child. FormField's
  `error` (a string) and the child input's `error` (a boolean) are
  conceptually paired; the host typically passes both together:

  ```tsx
  <FormField label="Email" name="email" error={errors.email}>
    <TextField name="email" value={v} onChange={setV} error={Boolean(errors.email)} />
  </FormField>
  ```

- The slot wrapper is a single `<div>` with no padding or background. Child
  inputs are rendered at the full slot width and own all their own visual
  surface.

- The `FormFieldContext` thread (`describedBy`) is the only data the wrapper
  passes to the child; visuals are independently owned per side.

---

## 6. Composition with field primitives

When FormField composes with one of the Batch B field primitives, the visual
flow is:

```
┌─────────────────────────────────────┐
│ Label  *                            │  ← --sf-field-label-* (and required-marker if required)
├─────────────────────────────────────┤
│ ┌─────────────────────────────────┐ │  ← slot wrapper (--sf-field-gap-rows above)
│ │ [child input rendered here]     │ │     child input owns its border/bg/focus
│ └─────────────────────────────────┘ │
├─────────────────────────────────────┤
│ Hint text OR Error message          │  ← --sf-field-hint-* OR --sf-field-error-*
└─────────────────────────────────────┘
```

Vertical rhythm is `gap-1.5` (6px) between each region. The slot child SHOULD
NOT add its own outer margin (it would compound with `gap-1.5`); it MAY add
internal padding (and does — every Batch B input has `px-3 py-2` or similar).

---

## 7. Open questions

1. **Required-marker positioning.** M1 renders the asterisk inline after the
   label text. Some design systems prefer the asterisk inside the label
   bracket prefix (`* Label`) or as a separate marker chip. Current contract:
   suffix-asterisk; revisit if user research surfaces a need.
2. **Hint + error simultaneous display.** The M1 implementation hides hint
   when error is present. Some design systems render both ("Email format
   required" + "Email is invalid"). Current contract: error-replaces-hint;
   revisit if usage patterns surface the need.
3. **`--sf-field-label-gap-marker` vs `--sf-field-required-marker-gap`.**
   The current token name implies "gap before the marker." A future provider
   that wants no asterisk gap (marker pressed against label text) might
   prefer `--sf-field-required-marker-margin-left`. Naming is non-critical;
   the contract picks one form and sticks with it.
4. **Tighter token mapping with TextField/SelectField/etc.** Some tokens
   (e.g., `--sf-field-label-fg`) MIGHT map directly to the global
   `--sf-color-text` token rather than a field-specific token. The split
   form gives provider themes per-field override; the merged form is
   simpler. Current contract keeps the field-specific token for symmetry
   with the rest of the field-primitive contracts.

---

## 8. Do / Don't

### Do

- Consume `--sf-field-*` via the recipes in §3.
- Keep label and error contrasts above WCAG 2.2 SC 1.4.3 (≥ 4.5:1).
- Use `aria-hidden="true"` on the required asterisk so AT doesn't announce
  "asterisk" twice (the child input's `required` attribute carries the
  programmatic signal; the asterisk is decorative redundancy per WCAG SC
  1.4.1).
- Honor `prefers-reduced-motion: reduce` for any future error-text
  transition (M1 has none; this is forward-compat).
- Pair the field-wrapper's `error` (string) with the child input's `error`
  (boolean) so the visual error treatment is consistent across both
  regions.

### Don't

- Don't put concrete hex values in this contract or in `FormField.tsx`.
  Hex values belong only in `forms.tokens.json`.
- Don't render both hint and error simultaneously without an explicit
  contract amendment (the M1 default is error-replaces-hint).
- Don't add padding / background to the slot wrapper — the child input
  owns its own surface, and adding wrapper-side padding would shift the
  child off-grid.
- Don't drive the error visual from a CSS class alone (`.has-error`); pair
  the visual with the child input's `aria-invalid` state per the
  Accessibility contract.

---

## 9. Parity notes

- **Blazor (future HarborlineForm / HarborlineFieldWrapper track):** consumes
  the same `--sf-field-*` family. Label primitive comes from the host
  Blazor `<label>` element with explicit `for` attribute.
- **React (this contract):** consumes via Tailwind utility classes; uses
  `@radix-ui/react-label` for the Label primitive (which renders a native
  `<label>` element with the standard `htmlFor` association).
- **Web Components (Phase M4, Lit):** TBD; consumes inside Shadow DOM
  with `::part(label)`, `::part(required-marker)`, `::part(hint)`,
  `::part(error)` exposure.

---

## References

- ADR 0017 §A1.3 — DataEntry (Forms) family contract scope
- [FormField.Semantic.md](./FormField.Semantic.md) — prop contract
- [FormField.Interaction.md](./FormField.Interaction.md) — behavioural contract
- [FormField.Accessibility.md](./FormField.Accessibility.md) — ARIA + describedby threading
- `_shared/design/tokens-guidelines.md` — `--sf-*` token naming conventions
- `_shared/design/tokens/forms.tokens.json` — concrete default values (new file)
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast

---

## Wave FR-1.4 — disabled visual treatment (Styling)

_Ruling: FR-1.4 (family-rulings-2026-06-11.md). Pattern: DataGrid #1022._
_Status: Draft._

### FR-1.4.1 Scope

FR-1.4 adds a `disabled` prop to FormField (see Semantic FR-1.4.2). This
section defines what the Styling contract owns vs. what it delegates.

### FR-1.4.2 FormField-chrome disabled treatment

When `disabled === true`, FormField applies dimming ONLY to its own chrome
regions (label row, required marker, hint/error message line). The child
input's dimmed visual is the child's own responsibility via its own
`--sf-input-*-disabled` tokens.

| Region | Disabled visual rule | Token |
|---|---|---|
| Label text | Reduced opacity / dimmed foreground | `--sf-field-label-fg-disabled` |
| Required marker | Reduced opacity (asterisk still visible but dimmed) | `--sf-field-required-marker-fg-disabled` |
| Hint text | Reduced opacity | `--sf-field-hint-fg-disabled` |
| Error text | Not applicable — a disabled field should not show an error message simultaneously. If both `disabled` and `error` are supplied, error text rendering is suppressed. | — |
| Slot wrapper | No styling change — transparent pass-through | — |

**Error + disabled precedence.** When a FormField has both `disabled={true}`
and `error="..."`, the error message is not rendered and the FormField chrome
renders in the disabled treatment. This is consistent with standard form
behaviour: a disabled field cannot be corrected by the user, so surfacing an
error message on it would be misleading.

### FR-1.4.3 New tokens (`--sf-field-*-disabled`)

Append to the `forms.tokens.json` `field` namespace:

```jsonc
{
  "field": {
    // ... existing tokens ...
    "label": {
      "fg": "#374151",
      "fgDisabled": "#9CA3AF"    // NEW — FR-1.4 (≥ 3:1 on white; decorative/non-interactive label)
    },
    "requiredMarker": {
      "fg": "#EF4444",
      "fgDisabled": "#FCA5A5"    // NEW — FR-1.4
    },
    "hint": {
      "fg": "#6B7280",
      "fgDisabled": "#D1D5DB"    // NEW — FR-1.4
    }
  }
}
```

Alias mapping:
- `--sf-field-label-fg-disabled` → `field.label.fgDisabled`
- `--sf-field-required-marker-fg-disabled` → `field.requiredMarker.fgDisabled`
- `--sf-field-hint-fg-disabled` → `field.hint.fgDisabled`

**Contrast note.** Disabled-state chrome does NOT need to meet WCAG SC 1.4.3
(≥ 4.5:1) — WCAG 2.2 §1.4.3 explicitly exempts "inactive user interface
components." The token values above are chosen to be visually recognisable as
dimmed while remaining perceptually distinct from the interactive state.

### FR-1.4.4 Visual state inventory addendum

Append to §4 visual state inventory:

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **disabled** | `disabled === true` | label, required-marker, hint — all dimmed | `--sf-field-*-disabled` tokens |
| **disabled + error** | both set | error message suppressed; chrome uses disabled treatment | disabled tokens only |

Mode-precedence (highest first): **disabled** > error > hint > default.
When `disabled` is active, it overrides the error visual for FormField's
own chrome.
