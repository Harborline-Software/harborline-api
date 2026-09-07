# SelectField — Styling Contract

- **Component:** SelectField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SelectField.Semantic.md) · [Interaction](./SelectField.Interaction.md) · [Accessibility](./SelectField.Accessibility.md)
- **Related contracts:** [TextField.Styling.md](./TextField.Styling.md) — SelectField's trigger button shares the trigger-chrome of `--sf-input-*` but introduces its own popover surface. [FormField.Styling.md](./FormField.Styling.md) — composing wrapper.
- **Reference implementation:** `packages/ui-react/src/components/forms/SelectField.tsx` (built on `@radix-ui/react-select`)
- **Catalog row:** #48 DropDownList (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

SelectField is the canonical single-value combobox of `@harborline-software/ui-react`,
built on `@radix-ui/react-select`. Unlike TextField / DateField /
NumberField (which wrap a single native `<input>`), SelectField has TWO
surfaces:

1. **Trigger** — a `<button>` element rendered in the form layout. Visually
   matches the input chrome of TextField (same border, focus, disabled,
   error treatments) so a row of mixed inputs reads as one form.
2. **Content (popover)** — a portalled dropdown panel rendered above other
   page chrome (`z-50`), containing the option list, item highlights, and
   selection indicator.

This contract names the `--sf-select-*` token surface for BOTH regions and
documents how the trigger inherits styling intent (but not tokens) from the
`--sf-input-*` family.

---

## 2. Token surface

SelectField exposes the following CSS custom properties (the `--sf-select-*`
family). Adapters MUST consume these tokens; adapters MUST NOT hard-code
values in component code.

### 2.1 Trigger button

The trigger inherits the **visual intent** of `--sf-input-*` but does NOT
literally consume the same tokens — it's a `<button>`, not an `<input>`.
The token surface is parallel-but-independent so providers can theme
trigger and input families separately if needed.

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-select-trigger-bg` | Trigger background fill | default + disabled | Default white; disabled tinted neutral |
| `--sf-select-trigger-fg` | Trigger text colour (selected-value display) | default | Pairs with `--sf-select-trigger-bg` at ≥ 4.5:1 |
| `--sf-select-trigger-placeholder-fg` | Placeholder text colour (when no value selected) | default | Pairs with `--sf-select-trigger-bg` at ≥ 4.5:1 |
| `--sf-select-trigger-border` | Border colour | default | Pairs with `--sf-select-trigger-bg` at ≥ 3:1 |
| `--sf-select-trigger-border-focus` | Border colour on `:focus-visible` | focus | Brand-accent; pairs at ≥ 3:1 |
| `--sf-select-trigger-ring-focus` | Focus-visible ring colour | focus | Typically matches border-focus |
| `--sf-select-trigger-border-error` | Border in error mode | error | Brand-error red; pairs at ≥ 3:1 |
| `--sf-select-trigger-border-focus-error` | Border in error + focus | error + focus | Darker brand-error |
| `--sf-select-trigger-ring-focus-error` | Ring in error + focus | error + focus | Matches border-focus-error |
| `--sf-select-trigger-bg-disabled` | Background when disabled | disabled | Neutral-50 |
| `--sf-select-trigger-opacity-disabled` | Opacity when disabled | disabled | Typically `0.6` |
| `--sf-select-trigger-radius` | Trigger corner radius | none | Typically `var(--sf-radius-md)` |
| `--sf-select-trigger-padding` | Trigger internal padding | none | Typically `0.5rem 0.75rem` (matches `--sf-input-padding-md`) |
| `--sf-select-trigger-font-size` | Trigger font size | none | Typically `0.875rem` (= `text-sm`) |
| `--sf-select-trigger-chevron-fg` | Chevron icon colour | default | Pairs against `--sf-select-trigger-bg` at ≥ 3:1 (decorative, but visible affordance) |

### 2.2 Popover (content panel)

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-select-content-bg` | Popover background | none | Typically `--sf-color-surface` (white) |
| `--sf-select-content-border` | Popover border | none | Pairs at ≥ 3:1 with the page background (NOT the popover background); the border must be perceivable against page chrome behind it |
| `--sf-select-content-radius` | Popover corner radius | none | Typically `var(--sf-radius-md)` |
| `--sf-select-content-shadow` | Popover drop-shadow | none | Typically `var(--sf-elevation-2)` — strong enough to visually float above the page |
| `--sf-select-content-padding` | Popover internal padding around the option list | none | Typically `0.25rem` (= `p-1`) — tight, options carry their own padding |
| `--sf-select-content-z-index` | Popover stack position | none | Numeric value; M1 baseline `50` |
| `--sf-select-content-side-offset` | Vertical gap between trigger and popover | none | Typically `4px` (M1 baseline) |

### 2.3 Option items

| Token | Semantic role | Varies by state | Notes |
|---|---|---|---|
| `--sf-select-item-bg` | Item background (default, not highlighted) | default | Inherits from `--sf-select-content-bg` (transparent) |
| `--sf-select-item-fg` | Item text colour (default) | default | Pairs with `--sf-select-content-bg` at ≥ 4.5:1 |
| `--sf-select-item-bg-highlighted` | Item background when highlighted (keyboard or hover) | highlighted | Tinted brand-accent (e.g., `--sf-color-primary-50`) |
| `--sf-select-item-fg-highlighted` | Item text in highlighted state | highlighted | Brand-accent text (e.g., `--sf-color-primary-900`); pairs with `--sf-select-item-bg-highlighted` at ≥ 4.5:1 |
| `--sf-select-item-padding` | Item internal padding | none | Typically `0.5rem 0.75rem` + `2rem` right (room for the selection indicator) |
| `--sf-select-item-radius` | Item corner radius | none | Typically `var(--sf-radius-sm)` — softens the inside of the popover |
| `--sf-select-item-indicator-fg` | Selection checkmark icon colour | selected | Brand-accent (e.g., `--sf-color-primary-600`); pairs at ≥ 3:1 |
| `--sf-select-item-indicator-size` | Selection checkmark size | none | Typically `0.875rem` (= `h-3.5 w-3.5`) |

### 2.4 Token additions to `forms.tokens.json`

Recommended sub-namespace addition (extends the FormField + input scaffold):

```jsonc
"select": {
  "trigger": {
    "bg": "#FFFFFF",
    "fg": "#111827",
    "placeholderFg": "#9CA3AF",
    "border": "#D1D5DB",
    "borderFocus": "#3B82F6",
    "ringFocus": "#3B82F6",
    "borderError": "#F87171",
    "borderFocusError": "#EF4444",
    "ringFocusError": "#EF4444",
    "bgDisabled": "#F9FAFB",
    "opacityDisabled": 0.6,
    "radius": "0.375rem",
    "padding": "0.5rem 0.75rem",
    "fontSize": "0.875rem",
    "chevronFg": "#9CA3AF"
  },
  "content": {
    "bg": "#FFFFFF",
    "border": "#E5E7EB",
    "radius": "0.375rem",
    "shadow": "0 10px 15px -3px rgba(0,0,0,0.1), 0 4px 6px -4px rgba(0,0,0,0.1)",
    "padding": "0.25rem",
    "zIndex": 50,
    "sideOffset": "4px"
  },
  "item": {
    "fg": "#374151",
    "bgHighlighted": "#EFF6FF",
    "fgHighlighted": "#1E40AF",
    "padding": "0.5rem 0.75rem 0.5rem 0.75rem",
    "paddingRight": "2rem",
    "radius": "0.25rem",
    "indicatorFg": "#2563EB",
    "indicatorSize": "0.875rem"
  },
  "_notes": {
    "contrast": "trigger.fg / placeholderFg on trigger.bg verified ≥ 4.5:1. content.border on a typical page background verified ≥ 3:1 (the border must be perceivable against the page, not just the popover). item.fgHighlighted on item.bgHighlighted verified ≥ 4.5:1. item.indicatorFg on item.bg verified ≥ 3:1.",
    "trigger-vs-input": "trigger.* mirrors input.* by design so a row of mixed inputs (TextField + SelectField + DateField) reads as one form. Providers MAY diverge them if branding requires it."
  }
}
```

---

## 3. Tailwind class recipes (M1 default layer)

### 3.1 Trigger

```
flex w-full items-center justify-between rounded-md border bg-white px-3 py-2 text-sm text-gray-900
focus:outline-none focus:ring-1

(error)    border-red-400 focus:border-red-500 focus:ring-red-500
(default)  border-gray-300 focus:border-blue-500 focus:ring-blue-500
(disabled) cursor-not-allowed bg-gray-50 opacity-60
```

Chevron icon (`<Select.Icon>` containing `<ChevronDown class="h-4 w-4 text-gray-400" aria-hidden="true" />`).

Placeholder render (`<Select.Value placeholder={<span className="text-gray-400">{placeholder}</span>}>`) — the placeholder text uses an inline `text-gray-400` for the muted treatment.

### 3.2 Popover content

```
z-50 min-w-[var(--radix-select-trigger-width)] overflow-hidden rounded-md border border-gray-200 bg-white shadow-lg
```

The `var(--radix-select-trigger-width)` keeps the popover at least as wide
as the trigger (Radix exposes the trigger width as a CSS custom property
inside the content's scope).

Viewport (`<Select.Viewport>`): `p-1`.

### 3.3 Option items

```
relative flex cursor-pointer select-none items-center rounded px-3 py-2 pr-8 text-sm text-gray-700 outline-none
data-[highlighted]:bg-blue-50 data-[highlighted]:text-blue-900
```

`data-[highlighted]` is Radix's data-state attribute (driven by keyboard or
pointer hover; both yield the same highlighted state).

Selection indicator (`<Select.ItemIndicator>` containing `<Check class="h-3.5 w-3.5 text-blue-600" aria-hidden="true" />`): rendered only on the currently-selected item; positioned absolutely at `right-2`.

---

## 4. Visual state inventory

### 4.1 Trigger states

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **default** | enabled, no error, no focus, popover closed | trigger | base tokens |
| **default focus-visible** | keyboard focus, popover closed | trigger border + ring | `--sf-select-trigger-border-focus` + `--sf-select-trigger-ring-focus` |
| **open** | popover is open (`data-state="open"` from Radix) | trigger | M1 baseline shows the focus-ring while open; no separate "open" treatment |
| **error** | `error === true`, popover closed | trigger border | `--sf-select-trigger-border-error` |
| **error focus-visible** | error + focus | trigger | `--sf-select-trigger-border-focus-error` + `--sf-select-trigger-ring-focus-error` |
| **disabled** | `disabled === true` | trigger | `--sf-select-trigger-bg-disabled` + `--sf-select-trigger-opacity-disabled` |
| **placeholder visible** | `value === ''` or matches no option | trigger value region | `--sf-select-trigger-placeholder-fg` |

### 4.2 Popover states

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **closed** | `data-state="closed"` | popover not rendered (or `display: none` per Radix) | n/a |
| **open** | `data-state="open"` | popover rendered above page chrome | `--sf-select-content-*` family |
| **animating in** | Radix's `data-state="open"` transition | popover (optional animation) | M1 has no transition (instant open/close); honors `prefers-reduced-motion` trivially |

### 4.3 Item states

| State | Condition | Affected region | Token / recipe |
|---|---|---|---|
| **default** | not highlighted, not selected | item | `--sf-select-item-bg` (transparent) + `--sf-select-item-fg` |
| **highlighted** | keyboard navigation or pointer hover lands on item (`data-highlighted`) | item | `--sf-select-item-bg-highlighted` + `--sf-select-item-fg-highlighted` |
| **selected** | item's value matches the current `value` prop | item indicator | `--sf-select-item-indicator-fg` (checkmark rendered) |
| **highlighted + selected** | both conditions | item | both treatments compose; checkmark + tinted background |

**State precedence (trigger):** disabled > error > focus > open > default.
**State precedence (item):** highlighted-state visual layers on top of
selected-state (the checkmark + tinted background coexist).

---

## 5. Composition with FormField

The trigger composes inside a FormField wrapper the same way TextField does:

- Trigger's full width (`w-full`) fills the FormField slot.
- Trigger's `id={name}` matches FormField's `htmlFor={name}` — label
  click focuses the trigger.
- Trigger reads `describedBy` from `useFormField()` and wires
  `aria-describedby` accordingly.

The popover is portalled OUT of the FormField subtree (Radix's
`<Select.Portal>` renders to `document.body` by default). The portal
detachment is fine for accessibility because Radix wires the `aria-*`
relationships explicitly.

---

## 6. Open questions

1. **Trigger vs Input token unification.** The trigger inherits visual
   intent from `--sf-input-*` but uses its own `--sf-select-trigger-*`
   namespace. Should they collapse to a single `--sf-input-*` family
   that both TextField and SelectField consume? Current contract: split,
   to preserve per-component override surface.
2. **Custom popover positioning.** Radix supports `side="top" | "right" |
   "bottom" | "left"` for the popover. M1 uses `position="popper"` (the
   default, bottom). Hosts that need custom positioning go straight to
   Radix; the contract does NOT expose positioning props.
3. **Selection indicator side.** Indicator is right-aligned (`right-2`).
   Some design systems prefer left-aligned checkmarks. Out of scope for
   M1 baseline; revisit on user research.
4. **Empty-state in popover.** When `options.length === 0`, the popover
   renders empty (no items). Hosts SHOULD conditionally render the
   SelectField only when there are options, OR a future amendment may add
   an `emptyMessage` slot.

---

## 7. Do / Don't

### Do

- Consume `--sf-select-*` via the recipes in §3.
- Match trigger visuals to TextField / DateField / NumberField so mixed-
  input form rows read as one row.
- Keep the popover's `z-50` so it renders above standard page chrome.
- Pair `data-[highlighted]:bg-blue-50` with `data-[highlighted]:text-blue-900`
  so the highlighted item is conveyed by BOTH background AND foreground
  (WCAG SC 1.4.1).
- Verify the popover's `--sf-select-content-border` against a TYPICAL
  page background, not just the popover background — the border must be
  perceivable in context.

### Don't

- Don't put concrete hex values in this contract or in `SelectField.tsx`.
  Hex values belong only in `forms.tokens.json`.
- Don't suppress the chevron icon — it's a load-bearing affordance
  (mouse users click the chevron to open; AT users hear the role as
  "combobox").
- Don't change the highlighted-state recipe from background+foreground
  pair to background-only — colour-alone would risk WCAG SC 1.4.1.
- Don't use `z-index` lower than the page's modal/drawer layer; the
  popover MUST render above standard chrome but BELOW any open modal
  (modal `z-index` ≥ 100 per Dialog.Styling).

---

## 8. Parity notes

- **Blazor (future HarborlineSelect track):** consumes the same
  `--sf-select-*` family. Same Radix-equivalent popover semantics expected.
- **React (this contract):** as documented, with Radix UI primitives.
- **Web Components (Phase M4, Lit):** TBD; the popover layer may be a
  `<sf-popover>` web component composing with the trigger.

---

## References

- ADR 0017 §A1.3 — DataEntry (Forms) family contract scope
- [SelectField.Semantic.md](./SelectField.Semantic.md) — prop contract
- [SelectField.Interaction.md](./SelectField.Interaction.md) — behavioural contract
- [SelectField.Accessibility.md](./SelectField.Accessibility.md) — ARIA + keyboard
- [TextField.Styling.md](./TextField.Styling.md) — sibling input styling
- [FormField.Styling.md](./FormField.Styling.md) — composing wrapper
- Radix UI — `@radix-ui/react-select`
- `_shared/design/tokens/forms.tokens.json` — concrete default values
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
