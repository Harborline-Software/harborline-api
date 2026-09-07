# SelectField — Accessibility Contract

- **Component:** SelectField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SelectField.Semantic.md) · [Interaction](./SelectField.Interaction.md) · [Styling](./SelectField.Styling.md)
- **Related contracts:** [FormField.Accessibility.md](./FormField.Accessibility.md) — describedby thread definition. [TextField.Accessibility.md](./TextField.Accessibility.md) — sibling input accessibility (label-input linkage, error pattern).
- **Reference implementation:** `packages/ui-react/src/components/forms/SelectField.tsx` (built on `@radix-ui/react-select`)
- **Catalog row:** #48 DropDownList (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

SelectField is the canonical single-value combobox, built on
`@radix-ui/react-select`. Most of its accessibility surface comes from
Radix's wiring of the WAI-ARIA combobox pattern — the trigger gets the
`combobox` role + `aria-expanded`, the popover gets the `listbox` role,
items get the `option` role, and the keyboard model (Arrow keys, Home /
End, type-ahead, Escape) is bound by Radix.

This contract names:

1. The Radix-inherited ARIA emissions + the WAI-ARIA combobox pattern
   citation.
2. The two SelectField-specific emissions on the trigger:
   `aria-invalid` (error mode) and `aria-describedby` (FormField thread).
3. The focus-management expectations across open / close transitions.
4. Three documented gaps against the full ARIA combobox pattern (e.g., no
   `aria-required` propagation, no `aria-activedescendant` opt-in).

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. ARIA structural roles

SelectField uses Radix's ARIA wiring. Radix's `@radix-ui/react-select`
implements the WAI-ARIA 1.2 combobox pattern explicitly (not as a native
HTML `<select>`).

| Element | Radix-emitted role | M1 baseline confirms |
|---|---|---|
| Trigger | `role="combobox"` + `aria-expanded` + `aria-controls` + `aria-haspopup="listbox"` | yes (via Radix) |
| Popover content | `role="listbox"` | yes (via Radix) |
| Each option | `role="option"` + `aria-selected` | yes (via Radix) |
| Selection indicator (checkmark icon) | `aria-hidden="true"` (decorative — selection is conveyed via `aria-selected` on the option) | yes (set explicitly in the implementation) |
| Chevron icon | `aria-hidden="true"` | yes (set explicitly) |

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships.
- WCAG 2.2 SC 4.1.2 Name, Role, Value.

**WAI-ARIA Authoring Practices** — Combobox pattern (single-select, listbox-
style popup): https://www.w3.org/WAI/ARIA/apg/patterns/combobox/

---

## 3. SelectField-specific emissions

The shipping implementation adds two emissions on top of Radix's defaults:

| Attribute | Condition | Source |
|---|---|---|
| `aria-invalid={true}` | `error === true` | SelectField forwards explicitly |
| `aria-describedby={describedBy}` | `useFormField()` returns a non-empty `describedBy` | SelectField forwards from `FormFieldContext` |

Both attributes land on Radix's `<Select.Trigger>` — the trigger is the
focused element, so this is the correct host for both.

**Standalone usage (no FormField wrapper).** `describedBy` resolves to
`undefined`; only `aria-invalid` is emitted (when applicable). Standalone
SelectField MUST be given an accessible name via one of:

- A sibling `<label for={name}>`.
- An `aria-label` directly on `<Select.Trigger>`.
- An `aria-labelledby` reference.

The shipping SelectField does NOT emit a default `aria-label`; standalone
usage MUST add labelling.

---

## 4. Label-input linkage

The trigger's `id={name}` matches the parent FormField's `htmlFor={name}`
(rendered on the `<label>`). Clicking the label focuses the trigger; the
native `<label>` → `id` association satisfies WCAG.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships.
- WCAG 2.2 SC 2.5.3 Label in Name.

---

## 5. Keyboard navigation

Radix's `@radix-ui/react-select` binds the full WAI-ARIA combobox keyboard
model. SelectField does NOT override any of it. The M1 baseline (Radix
defaults):

### 5.1 Trigger focused, popover closed

| Key | Behaviour |
|---|---|
| Tab | Move focus out (in tab order). |
| Shift+Tab | Reverse. |
| Enter / Space | Open the popover; focus moves to the currently-selected option (or first option if none selected). |
| Arrow Down | Open the popover (same as Enter / Space). |
| Arrow Up | Open the popover (same; some browsers / locales may differ). |
| Type-ahead | Typing a letter opens the popover and highlights the first option whose label starts with that letter. |

### 5.2 Popover open

| Key | Behaviour |
|---|---|
| Arrow Down | Move highlight to next option (wraps from last to first). |
| Arrow Up | Move highlight to previous option (wraps from first to last). |
| Home | Highlight the first option. |
| End | Highlight the last option. |
| Letter / digit keys | Type-ahead: highlight the next option whose label starts with the typed string (multi-letter type-ahead per WAI-ARIA practice). |
| Enter | Commit the highlighted option (fires `onValueChange` and closes popover). |
| Space | Same as Enter. |
| Escape | Close the popover WITHOUT committing; focus returns to the trigger. |
| Tab | Commit the highlighted option AND close popover (Radix behaviour; some implementations vary). |

**WCAG citations:**
- WCAG 2.2 SC 2.1.1 Keyboard.
- WCAG 2.2 SC 2.1.2 No Keyboard Trap (Escape exits; Tab exits).

---

## 6. Focus management

Radix manages focus across the open / close lifecycle:

| Event | Focus destination |
|---|---|
| User opens popover (Enter / Space / Arrow / type-ahead) | Focus moves into the popover; the currently-selected option (or the first option) receives focus. |
| User highlights a different option via Arrow keys | The option receives focus (or `aria-activedescendant` moves — Radix uses managed-focus pattern). |
| User commits an option (Enter / Space / Tab) | Focus returns to the trigger. |
| User cancels via Escape | Focus returns to the trigger. |
| User clicks outside the popover | Popover closes; focus returns to the previous focus point per Radix dismissal logic. |

The focus return on close is **the** load-bearing focus-management
requirement — the user must end up back on the trigger so subsequent Tab
moves forward predictably.

**WCAG citations:**
- WCAG 2.2 SC 2.4.3 Focus Order.
- WCAG 2.2 SC 3.2.1 On Focus, SC 3.2.2 On Input.

---

## 7. Visible focus ring

The trigger consumes the same focus-ring recipe as TextField:
`focus:outline-none focus:ring-1` with `focus:ring-blue-500` (default) or
`focus:ring-red-500` (error). The visible indicator is mandatory.

The popover items have an `outline-none` rule (no per-item focus ring); the
highlighted state (`data-[highlighted]:bg-blue-50`) is the visible indicator
inside the listbox. This satisfies WCAG SC 2.4.7 because the focus state
is still visually distinguishable.

**WCAG citation:** WCAG 2.2 SC 2.4.7 Focus Visible.

---

## 8. Color contrast

Per [SelectField.Styling §"Visual state inventory"](./SelectField.Styling.md):

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Trigger text on trigger background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Placeholder text on trigger background | 4.5:1 | WCAG 2.2 SC 1.4.3 (placeholder is text) |
| Trigger border on host surface | 3:1 | WCAG 2.2 SC 1.4.11 |
| Trigger focus ring on host surface | 3:1 | WCAG 2.2 SC 1.4.11 |
| Item text on popover background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Item text-highlighted on item bg-highlighted | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Selection-indicator icon on item background | 3:1 | WCAG 2.2 SC 1.4.11 |
| Popover border on page background | 3:1 | WCAG 2.2 SC 1.4.11 |

**Color is not the only channel.** Selection is conveyed via
`aria-selected="true"` (programmatic) + the checkmark icon (visual) + the
trigger value display (text content reflects the selection). **WCAG SC
1.4.1 satisfied.**

---

## 9. Touch targets

| Control | Default size at M1 |
|---|---|
| Trigger | `px-3 py-2 text-sm` → effective height ~36px ✓ |
| Each item | `px-3 py-2 text-sm` → effective height ~36px ✓ |
| Chevron icon | not interactive (the whole trigger is the click target) |
| Selection indicator | not interactive (decorative) |

All interactive controls clear the 24 × 24 minimum.

**WCAG citation:** WCAG 2.2 SC 2.5.8 Target Size (Minimum).

---

## 10. Required state

When the host needs the field marked as required, the host MUST set the
parent FormField's `required={true}` AND set `aria-required="true"` on the
`<Select.Trigger>` manually (Radix does NOT auto-propagate required from
the wrapper). **Gap G1** (§13) — SelectField does NOT expose a `required`
prop; the host wires manually.

A follow-on PR SHOULD add a `required?: boolean` prop to SelectField's
Semantic contract that forwards to `aria-required` on the trigger.

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships.

---

## 11. Reduced motion

Radix's popover open/close animation honors `prefers-reduced-motion` via
its built-in motion-disable behaviour. SelectField itself adds no
additional animation in M1.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 12. Empty popover (`options.length === 0`)

When the host passes an empty `options` array:

- The popover opens with no items.
- AT users hear an empty listbox.
- No "no options" message is rendered in M1. **Gap G2** (§13) — a
  follow-on PR SHOULD add an `emptyMessage` slot, OR the contract SHOULD
  document that hosts MUST conditionally render SelectField only when
  options exist.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages — an empty listbox
is technically valid but poor UX.

---

## 13. Known gaps

| # | Gap | Fix path |
|---|---|---|
| G1 | No `required` prop on SelectField — hosts manually set `aria-required` | Follow-on PR adds `required?: boolean` to Semantic |
| G2 | Empty popover renders no message; AT user hears empty listbox | Follow-on PR adds `emptyMessage?: string` OR contract documents conditional-render requirement |
| G3 | No `multiple` selection variant | Out of scope for M1; future MultiSelect component |
| G4 | No `searchable` / `filterable` variant for long option lists | Future Autocomplete component |
| G5 | Standalone usage (no FormField wrapper) requires manual labelling | Documented in §3 |

---

## 14. Do / Don't

### Do

- Wrap in a FormField for the standard label + describedby + error
  treatment.
- Pair `error={Boolean(errors[name])}` parallel to FormField's
  `error={errors[name]}` (string).
- Keep `aria-hidden="true"` on the chevron and the selection indicator —
  Radix announces selection via `aria-selected` on the option, not via
  the icon.
- Verify the popover border contrast against page chrome (not just the
  popover background) so the popover edge is visible.
- Let Radix manage focus across open/close — do NOT override.

### Don't

- Don't omit `aria-invalid={true}` when the parent FormField's `error` is
  set.
- Don't render an empty popover without an `emptyMessage` — either guard
  with a conditional render or pass a sentinel item.
- Don't suppress the chevron — it's a load-bearing affordance for
  pointer users and a contributor to the combobox-pattern recognisability
  for AT users (some AT vendors lean on the visible chevron's role hint).
- Don't manually emit `aria-expanded` / `aria-controls` / `aria-haspopup`
  on the trigger — Radix wires these. Manual emission would conflict.

---

## 15. Parity notes

- **Blazor (future HarborlineSelect track):** consumes the same accessibility
  contract. Same WAI-ARIA combobox pattern; Blazor would build on
  equivalent Radix-style primitives or implement the pattern manually.
- **React (this contract):** as documented, with Radix UI primitives.
- **Web Components (Phase M4, Lit):** TBD; the WC track may build a
  `<sf-select>` web component implementing the combobox pattern manually.

---

## References

- ADR 0017 §A1.3 — DataEntry (Forms) family contract scope
- [SelectField.Semantic.md](./SelectField.Semantic.md) — prop contract
- [SelectField.Interaction.md](./SelectField.Interaction.md) — behavioural contract
- [SelectField.Styling.md](./SelectField.Styling.md) — token surface + visual states
- [FormField.Accessibility.md](./FormField.Accessibility.md) — describedby thread definition
- [TextField.Accessibility.md](./TextField.Accessibility.md) — sibling input contract
- Radix UI — `@radix-ui/react-select`
- WAI-ARIA Authoring Practices — Combobox pattern
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `combobox`, `listbox`, `option`, `aria-expanded`, `aria-controls`, `aria-haspopup`, `aria-selected`, `aria-describedby`, `aria-invalid`, `aria-required`
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.1.2 No Keyboard Trap
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.3 Focus Order
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.5.3 Label in Name
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
- WCAG 2.2 SC 3.2.1 On Focus
- WCAG 2.2 SC 3.2.2 On Input
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 4.1.3 Status Messages
