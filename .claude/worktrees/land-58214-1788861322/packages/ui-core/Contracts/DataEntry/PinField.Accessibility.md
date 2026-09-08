# PinField — Accessibility Contract

- **Component:** PinField
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PinField.Semantic.md) · [Interaction](./PinField.Interaction.md) · [Accessibility](./PinField.Accessibility.md) · [Styling](./PinField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/PinField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

PinField renders `length` individual `<input>` elements in a `role="group"`
container. Each cell is individually labeled. This contract names the ARIA
surface, keyboard model, and known gaps.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| Root `<div>` | `role="group"` | `aria-label={ariaLabel}` (default `"PIN"`) |
| Each `<input type="text">` | implicit `textbox` | `aria-label="{ariaLabel} digit {i+1}"` |

---

## 3. Group label

`role="group"` with `aria-label` provides AT users with context for the
multi-cell widget. Default: `"PIN"`. Customisable via `aria-label` prop.

---

## 4. Cell labels

Each cell has `aria-label="{ariaLabel} digit {i+1}"` — e.g., `"PIN digit 1"`,
`"PIN digit 2"`. AT announces the cell's label when focus enters.

---

## 5. Focus advance and AT experience

When the user types a character and focus advances to the next cell, AT may or
may not announce the next cell's label. The programmatic `focus()` call triggers
a focus event which AT should announce as the new cell's label.

> **Gap A-1:** Automatic focus advance (`.focus()`) may cause AT to lose track
> of the current focus or announce the wrong label on rapid entry. This is a
> known limitation of multi-cell PIN patterns. Testing with NVDA/JAWS/VoiceOver
> recommended.

---

## 6. Error state

When `error === true`, all cells receive `border-red-400 focus:ring-red-500`.
No `aria-invalid` is set on any cell.

> **Gap A-2:** No `aria-invalid` emitted when `error === true`. AT users do
> not receive a programmatic invalid-state announcement. Fix: set
> `aria-invalid={error ? true : undefined}` on each cell input.

---

## 7. No `aria-describedby`

PinField has no hint or error message text. There is no `aria-describedby`
on any cell.

> **Gap A-3:** No error message surface. When `error === true`, AT knows only
> via the red border (visual channel). Add an error message text + `role="alert"`
> or pair with a FormField's error message.

---

## 8. Disabled state

Native `disabled` on each cell. AT announces each as "unavailable". Cells
removed from tab order.

---

## 9. Keyboard navigation

| Key | AT interaction |
|---|---|
| Tab | Enter/leave PinField; within the group, Tab moves through individual cells |
| Arrow keys | Move focus between cells without announcing in most AT |
| Backspace | Clear and retreat; AT announces new cell |

The use of Tab to move between cells means entering a 6-digit PIN requires 6
Tab presses to pass through it from the outside. Arrow keys provide a
faster-but-less-obvious navigation path.

> **Gap A-4:** No `tabIndex="-1"` on sibling cells with roving focus. A single
> Tab stop for the whole group (with arrow-key internal navigation) would be
> more AT-friendly. This is the recommended ARIA pattern for similar widgets.

---

## 10. `inputMode`

`inputMode="numeric"` for `type='numeric'`; `inputMode="text"` for
`type='alphanumeric'`. Opens appropriate mobile keyboard.

---

## 11. Focus ring

```
focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-1
```

`ring-2` satisfies WCAG 2.4.7 and 2.4.13. `ring-offset-1` provides additional
visual separation between adjacent cells.

---

## 12. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-1 | Focus advance under AT may cause announce confusion | WCAG SC 2.4.3 | Test with NVDA/JAWS; consider `aria-live` announcements |
| A-2 | No `aria-invalid` on cells when `error === true` | WCAG SC 3.3.1, SC 4.1.2 | Add `aria-invalid={error ? true : undefined}` to cells |
| A-3 | No error message text | WCAG SC 3.3.1 | Add error prop as string + `role="alert"` |
| A-4 | Tab enters each cell individually (6 stops for 6-digit PIN) | WCAG SC 2.1.1 | Consider roving tabIndex for single-entry pattern |
