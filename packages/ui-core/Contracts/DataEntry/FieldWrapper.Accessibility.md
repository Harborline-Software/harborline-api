# FieldWrapper — Accessibility Contract

- **Component:** FieldWrapper
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FieldWrapper.Semantic.md) · [Interaction](./FieldWrapper.Interaction.md) · [Accessibility](./FieldWrapper.Accessibility.md) · [Styling](./FieldWrapper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FieldWrapper.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

FieldWrapper provides structural accessibility scaffolding for form fields: a
`<label>` for name association, an `<ErrorLabel role="alert">` for live error
announcement, and optional "(optional)" suffix text. It does NOT provide an
`aria-describedby` thread to children (contrast: `FormField` does, via
`FormFieldContext`).

---

## 2. Label association

`<Label htmlFor={id}>` creates the programmatic label-input linkage. The child
input MUST have `id={id}` for the linkage to be complete.

**Host responsibility:** the `id` prop on FieldWrapper must match the child
input's `id`. FieldWrapper does not enforce this.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 4.1.2 Name, Role, Value

---

## 3. Optional marker

When `optional === true`, the Label renders:
```tsx
<span className="ml-1 text-xs text-muted-foreground font-normal">(optional)</span>
```

This text is visible and readable by AT (no `aria-hidden`). AT reads the full
label text as e.g. "Email address (optional)".

---

## 4. Error announcement

`<ErrorLabel>` renders `<p role="alert">`. ARIA `alert` role is a live region
with implicit `aria-live="assertive"` and `aria-atomic="true"`. When the error
text enters the DOM, AT announces it immediately, interrupting any current
speech.

**Caveat:** rapid error-message changes (e.g. real-time validation on every
keystroke) can be disruptive. Hosts should debounce or defer error display to
blur/submit events.

**WCAG citations:**
- WCAG 2.2 SC 3.3.1 Error Identification
- WCAG 2.2 SC 4.1.3 Status Messages

---

## 5. Hint text

`<HintLabel>` renders a `<p>` with no ARIA attributes. It is visible to AT as
regular paragraph text but is NOT wired to the child input via
`aria-describedby`. This means AT does NOT automatically associate the hint
with the input — the hint is announced only as the user reads through the
page, not when focusing the input.

This is a known gap (see G1 below).

---

## 6. Border treatment — no programmatic error signal to children

The error border is applied via CSS descendant selectors
(`[&>input]:border-destructive`). This is a visual-only signal on the child
input. It does NOT set `aria-invalid` on the child input. Hosts that use
FieldWrapper and need `aria-invalid` on the input must set it themselves (or
use a child component that sets it, e.g. SearchField sets `aria-invalid`
based on its own `error` prop).

**WCAG citations:**
- WCAG 2.2 SC 1.4.1 Use of Color (color cannot be the only error signal)
- WCAG 2.2 SC 3.3.1 Error Identification

---

## 7. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | Hint `<p>` has no `id` and is not wired via `aria-describedby` to the child input | Medium | Assign `id="{id}-hint"` to hint `<p>`, pass to children via context or `React.cloneElement` |
| G2 | CSS border-based error signalling does not set `aria-invalid` on child input | High | Host must set `aria-invalid` on child input, OR FieldWrapper could broadcast error state via context |
| G3 | `role="alert"` on ErrorLabel is assertive — fires on every re-render with same text | Medium | Wrap in a condition so the alert fires only on new/changed error messages |
