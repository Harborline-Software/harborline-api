# DateField — Accessibility Contract

- **Component:** DateField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DateField.Semantic.md) · [Interaction](./DateField.Interaction.md) · [Styling](./DateField.Styling.md)
- **Related contracts:** [TextField.Accessibility.md](./TextField.Accessibility.md) — DateField inherits the same ARIA + describedby pattern; this contract documents the date-specific deltas. [FormField.Accessibility.md](./FormField.Accessibility.md) — describedby thread definition.
- **Reference implementation:** `packages/ui-react/src/components/forms/DateField.tsx`
- **Catalog row:** #37 DateInput (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

DateField is a thin wrapper around a native HTML `<input type="date">`.
Native browser semantics provide the date-picker keyboard model, focus
ring, AT exposure, and the implicit `textbox` (or browser-specific
`spinbutton` / custom) role. This contract names the **deltas** from
TextField.Accessibility:

1. The native date picker's keyboard model (arrow keys for day navigation
   inside the picker dropdown).
2. The `min` / `max` constraint announcement.
3. Locale-dependent AT announcement of the date value.

For shared inherited behaviour (label-input linkage, `aria-describedby`
threading, `aria-invalid` on error, native disabled state), see
[TextField.Accessibility.md](./TextField.Accessibility.md).

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. ARIA structural role

DateField renders a native `<input type="date">`. The implicit role is
`textbox` per WAI-ARIA 1.2 — browsers vary slightly on this (Chrome
exposes "date picker" as a custom AT label; Firefox uses generic
`textbox`). The visible-label binding via the parent FormField provides
the field's accessible name.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 3. Inherited behaviour

The following items inherit from TextField.Accessibility without change:

| Item | Inherited section |
|---|---|
| Label-input linkage via `id={name}` ↔ FormField's `htmlFor={name}` | [TextField.Accessibility §3](./TextField.Accessibility.md) |
| `aria-describedby` from `FormFieldContext` | [TextField.Accessibility §4](./TextField.Accessibility.md) |
| `aria-invalid={true}` when `error={true}` | [TextField.Accessibility §5](./TextField.Accessibility.md) |
| Native `disabled` semantics (skip in tab order, AT announces "disabled") | [TextField.Accessibility §6](./TextField.Accessibility.md) |
| Focus management — controlled `value` re-renders don't lose focus | [TextField.Accessibility §8](./TextField.Accessibility.md) |
| Visible focus ring (`focus:ring-1` overlay) | [TextField.Accessibility §8](./TextField.Accessibility.md) |
| Color contrast minimums on input chrome | [TextField.Accessibility §9](./TextField.Accessibility.md) |

---

## 4. Keyboard navigation — date-specific deltas

DateField uses the native `<input type="date">` keyboard model. Browsers
differ slightly; the M1 baseline accepts the native variance:

### 4.1 Inside the input field (caret position)

| Key | Behaviour |
|---|---|
| Tab | Move focus into the input. |
| Shift+Tab | Move focus out backwards. |
| Number keys (0-9) | Type into the date field. Browsers parse YYYY-MM-DD or locale-dependent formats. |
| Arrow Up / Arrow Down | Increment / decrement the focused segment (month, day, year). |
| Arrow Left / Arrow Right | Move between segments (year ↔ month ↔ day). |
| Space / Enter | On WebKit-based browsers, opens the calendar picker. |

### 4.2 Inside the calendar picker dropdown (when open)

The browser-native picker has its OWN keyboard model (which the input does
not bind directly):

| Key | Behaviour (browser-typical) |
|---|---|
| Arrow keys | Navigate days in the calendar grid. |
| Page Up / Page Down | Navigate months. |
| Shift+Page Up / Shift+Page Down | Navigate years. |
| Enter | Commit the highlighted date. |
| Escape | Close the picker without committing. |

**This keyboard model is not contractual at the DateField layer** — it's a
browser implementation detail. DateField does not enhance, override, or
document it as a contract guarantee. A future custom DatePicker (Semantic
§7) would document its keyboard model explicitly.

**WCAG citations:**
- WCAG 2.2 SC 2.1.1 Keyboard.
- WCAG 2.2 SC 2.1.2 No Keyboard Trap (Escape closes the picker).

---

## 5. `min` / `max` constraint

When `min` and/or `max` are supplied:

- The browser enforces the constraint silently — typing or picking a date
  outside `[min, max]` does NOT commit (the input value doesn't update).
- The browser SHOULD expose the constraint to AT (e.g., "date field, minimum
  2026-01-01, maximum 2026-12-31"), but support varies per browser.
- DateField does NOT add a visual or `aria-describedby` hint about the
  constraint in M1. **Gap G1** (§9) — a follow-on PR SHOULD add an
  automatic hint pattern ("Enter a date between {min} and {max}") that
  threads into the parent FormField's `hint` slot.

**WCAG citations:**
- WCAG 2.2 SC 3.3.2 Labels or Instructions — the constraint is an
  instruction that SHOULD be conveyed to the user.
- WCAG 2.2 SC 4.1.3 Status Messages — silent rejection is poor UX; a
  future amendment may add error-on-invalid feedback.

---

## 6. Locale-dependent value display

The value attribute is always ISO-8601 (`YYYY-MM-DD`), but the visible
display in the input chrome is **locale-dependent**:

- US locale: `MM/DD/YYYY`
- UK / European locale: `DD/MM/YYYY`
- ISO locale: `YYYY-MM-DD`

AT users hear the locale-formatted date. The contract treats this as
correct behaviour — users hear dates in their local format. No special
M1 emission is needed.

**WCAG citation:** WCAG 2.2 SC 3.1.1 Language of Page — locale is the
host document's `lang` attribute.

---

## 7. Touch targets

| Size | Approximate height | WCAG 2.2 SC 2.5.8 status |
|---|---|---|
| M1 baseline (`md` only) | ~36px | meets the 24 × 24 minimum |

The calendar-icon affordance (`::-webkit-calendar-picker-indicator` on
WebKit) is small (~16 × 16) but it's a supplementary affordance —
clicking anywhere on the input also opens the picker (in browsers that
support it). The effective hit target is the full input width × 36px.

**WCAG citation:** WCAG 2.2 SC 2.5.8 Target Size (Minimum).

---

## 8. Reduced motion

The browser-native date picker honors the OS-level reduced-motion setting
automatically (the picker's open/close animation is browser-managed).
DateField itself has no animations.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 9. Known gaps

| # | Gap | Fix path |
|---|---|---|
| G1 | Min/max constraint not surfaced as a hint or error message | Follow-on PR adds an automatic hint pattern threading into FormField's `hint` slot |
| G2 | Browser-native picker variance — different browsers expose different keyboard models inside the picker | Council decides whether to build a custom DatePicker (Semantic §7); M1 accepts native variance |
| G3 | Silent rejection on out-of-range value | Follow-on PR adds a visual "out of range" treatment |
| G4 | No `autocomplete="bday"` / `"bday-day"` / etc. exposed | Future amendment may add `autocomplete?: string` prop (WCAG SC 1.3.5) |

---

## 10. Do / Don't

### Do

- Treat DateField as a thin specialisation of TextField — inherit all
  ARIA + keyboard + describedby behaviour.
- Use ISO-8601 dates for `value`, `min`, `max` — the HTML spec requires
  this regardless of locale.
- Wrap in a FormField for the standard label / hint / error treatment.
- Verify the parent FormField's `hint` slot is populated when `min`/`max`
  are non-trivial — users benefit from explicit constraint communication.

### Don't

- Don't try to enhance the browser-native picker's keyboard model — that
  belongs in a custom DatePicker (out of scope for M1).
- Don't use `aria-label` to spell out the date format ("Date in MM/DD/YYYY
  format"); the locale-aware display already conveys this.
- Don't suppress the calendar-icon affordance (Chrome / Safari) — it's a
  visible affordance that helps mouse users.
- Don't omit `aria-invalid={true}` when the parent FormField's `error` is
  set — the inherited TextField rule applies.

---

## 11. Parity notes

- **Blazor (future HarborlineDateField track):** consumes the same
  accessibility contract. Native `<input type="date">` element.
- **React (this contract):** as documented.
- **Web Components (Phase M4, Lit):** TBD; native `<input type="date">`
  in the shadow tree.

---

## References

- [DateField.Semantic.md](./DateField.Semantic.md) — prop contract
- [DateField.Interaction.md](./DateField.Interaction.md) — behavioural contract
- [DateField.Styling.md](./DateField.Styling.md) — token surface + visual states
- [TextField.Accessibility.md](./TextField.Accessibility.md) — inherited ARIA behaviour
- [FormField.Accessibility.md](./FormField.Accessibility.md) — describedby thread definition
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `textbox`, `aria-describedby`, `aria-invalid`
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
- WCAG 2.2 SC 3.3.2 Labels or Instructions
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 4.1.3 Status Messages
