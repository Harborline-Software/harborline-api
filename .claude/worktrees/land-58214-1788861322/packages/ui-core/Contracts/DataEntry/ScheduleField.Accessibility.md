# ScheduleField — Accessibility Contract

- **Component:** ScheduleField
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ScheduleField.Semantic.md) · [Interaction](./ScheduleField.Interaction.md) · [Accessibility](./ScheduleField.Accessibility.md) · [Styling](./ScheduleField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ScheduleField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

ScheduleField uses the native `<fieldset>` + `<legend>` HTML pattern to group
related form controls. Each sub-control has a `<label>`. This contract names
the ARIA surface and known gaps.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| `<fieldset>` | (native group) | Native fieldset — no ARIA role needed |
| `<legend>` (when `label` supplied) | (native legend) | AT announces legend text as the group label |
| Date `<label>` | (linked) | `htmlFor="{id}-date"`; text `"Date"` |
| Date `<input type="date">` | implicit | Native date input |
| Start time `<label>` | (linked) | `htmlFor="{id}-start"`; text `"Start time"` |
| Start time `<input type="time">` | implicit | Native time input |
| End time `<label>` | (linked) | `htmlFor="{id}-end"`; text `"End time"` |
| End time `<input type="time">` | implicit | Native time input |
| All-day `<label>` (wrapper) | (linked) | Wraps the checkbox; text `"All day"` |
| All-day `<input type="checkbox">` | implicit `checkbox` | |

---

## 3. `<fieldset>` + `<legend>`

Using a native `<fieldset>` with `<legend>` is the WCAG-preferred approach for
grouping related form controls. AT announces the legend text as the group label
when focus enters any child control.

**WCAG citations:** WCAG 2.2 SC 1.3.1, SC 4.1.2.

---

## 4. Sub-control label linkage

Each visible sub-control has a linked `<label>`:

| Input | `<label>` text |
|---|---|
| Date | `"Date"` |
| Start time | `"Start time"` |
| End time | `"End time"` |

Labels are `block text-xs text-gray-500` — small and subdued. AT announces
these correctly.

The all-day checkbox uses a wrapping `<label>` pattern with the checkbox
inside it. This is an acceptable labelling approach.

---

## 5. Conditional control visibility

When `allDay` toggles to `true`, start and end time inputs are unmounted from
the DOM (not just hidden). AT removes them from the virtual buffer.

> **Gap A-1:** No live region announces the appearance/disappearance of time
> inputs when `allDay` changes. AT users may not realize the time controls have
> been removed. Consider adding an `aria-live="polite"` region.

---

## 6. Error state

No `aria-invalid` on any sub-control. No error messages.

> **Gap A-2:** No error state ARIA surface. The end-time `min` constraint
> relies on browser native validation bubbles which may not be announced by all
> AT implementations.

---

## 7. Disabled state

All sub-controls receive native `disabled` when `disabled === true`. AT
announces each as "unavailable".

**WCAG citation:** WCAG 2.2 SC 4.1.2.

---

## 8. Focus rings

All `<input>` elements use:

```
focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500
```

`ring-1` (1px) — satisfies WCAG 2.4.7 (Focus Visible) but falls short of
WCAG 2.4.13 (Focus Appearance) which requires a 2px minimum.

> **Gap A-3:** `ring-1` does not satisfy WCAG 2.4.13. Upgrade to `ring-2`.

The all-day checkbox uses:

```
focus:ring-blue-500
```

No `ring-2` specified explicitly; relies on the Tailwind default focus-ring
width for checkboxes.

---

## 9. `autocomplete`

Date and time inputs do not carry `autocomplete` attributes. No gaps for
these types (browsers do not autofill date/time fields in the same way as
text fields).

---

## 10. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-1 | No live region for allDay visibility change | WCAG SC 4.1.3 | Add `aria-live="polite"` region |
| A-2 | No `aria-invalid` on sub-controls | WCAG SC 3.3.1, SC 4.1.2 | Add error props per sub-control |
| A-3 | `ring-1` on inputs — WCAG 2.4.13 non-compliant | WCAG SC 2.4.13 | Upgrade to `ring-2` |
