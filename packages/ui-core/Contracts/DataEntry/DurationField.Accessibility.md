# DurationField — Accessibility Contract

- **Component:** DurationField
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DurationField.Semantic.md) · [Interaction](./DurationField.Interaction.md) · [Accessibility](./DurationField.Accessibility.md) · [Styling](./DurationField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DurationField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

DurationField renders 2–3 native `<input type="number">` elements. Each
segment receives an explicit `aria-label` to identify its purpose to AT.
This contract names the label approach, focus ring, and known gaps.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| Root flex `<div>` | (none) | No group role in the current implementation |
| Hours `<input type="number">` | implicit `spinbutton` | `aria-label="Hours"` |
| Minutes `<input type="number">` | implicit `spinbutton` | `aria-label="Minutes"` |
| Seconds `<input type="number">` | implicit `spinbutton` | `aria-label="Seconds"` |
| Label `<label>` (when `label` prop supplied) | (linked) | `sr-only`; linked via `htmlFor={id}` to the first segment input |
| Colon `<span>` | (decorative) | No `aria-hidden` set — visual separator; plain text |

---

## 3. Segment labels

Each segment input has `aria-label`:

| Segment | `aria-label` |
|---|---|
| Hours | `"Hours"` |
| Minutes | `"Minutes"` |
| Seconds | `"Seconds"` |

AT announces each segment by its label when focused. The AT user can understand
the field's structure by navigating through the segments.

---

## 4. Optional sr-only label

When `label` is supplied, a `<label htmlFor={id}>` with `className="sr-only"`
is rendered. It links to the first segment's input and provides context for the
whole field. AT will announce the `label` text when focus enters that first
segment.

> **Gap A-1:** Only the first segment carries the linked `id`. AT users
> navigating via Tab will not hear the field-level label when they focus the
> second or third segment. Consider wrapping in a `<fieldset>` + `<legend>` or
> adding a `role="group"` with `aria-labelledby` to the root container.

---

## 5. `aria-describedby`

Not wired. DurationField reads `ctx?.inputId` from `FormFieldContext` but does
not read `describedBy`.

> **Gap A-2:** No `aria-describedby` connection. Hint and error messages from
> a parent FormField are not announced on focus.

---

## 6. Error state

No `aria-invalid` is emitted. DurationField has no `error` prop.

> **Gap A-3:** No error state ARIA surface. Add `error` prop + `aria-invalid`
> on affected segment(s).

---

## 7. Disabled state

Native `disabled` is set on each segment input. AT announces each as
"unavailable". Inputs removed from tab order.

**WCAG citation:** WCAG 2.2 SC 4.1.2.

---

## 8. Focus ring

```
focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
```

`ring-2` (2px) on each segment. Satisfies WCAG 2.4.7 and 2.4.13.

**WCAG citations:** WCAG 2.2 SC 2.4.7, SC 2.4.13.

---

## 9. Colon separators

The colon separators (`<span>`) are not given `aria-hidden`. They are plain
text characters. AT may read them aloud. Marking them `aria-hidden="true"` would
clean up the reading experience.

> **Gap A-4:** Colon separators not marked `aria-hidden`. Add `aria-hidden="true"`
> to prevent unnecessary "colon" announcements by some screen readers.

---

## 10. Known gaps

| # | Gap | WCAG citation | Fix path |
|---|---|---|---|
| A-1 | Only first segment linked to field-level label | WCAG SC 1.3.1, SC 4.1.2 | Wrap in `<fieldset>`/`<legend>` or `role="group"` with `aria-labelledby` |
| A-2 | No `aria-describedby` wired | WCAG SC 1.3.1, SC 3.3.2 | Read `describedBy` from FormFieldContext and apply to first or all segments |
| A-3 | No `aria-invalid` / error state | WCAG SC 3.3.1, SC 4.1.2 | Add `error` prop |
| A-4 | Colon separators not `aria-hidden` | Minor AT noise | Add `aria-hidden="true"` to separator spans |
