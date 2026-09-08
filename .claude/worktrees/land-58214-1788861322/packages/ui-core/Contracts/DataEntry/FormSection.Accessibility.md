# FormSection — Accessibility Contract

- **Component:** FormSection
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FormSection.Semantic.md) · [Interaction](./FormSection.Interaction.md) · [Accessibility](./FormSection.Accessibility.md) · [Styling](./FormSection.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormSection.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

FormSection provides a semantically grouped form section using `<fieldset>` +
`<legend>`. AT users hear the section name announced when focus moves into any
field within the section. The visible `<h3>` uses `aria-hidden="true"` to
avoid double-announcement.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| `<fieldset>` | `group` (implicit) | Groups all child form controls |
| `<legend className="sr-only">` | legend | Announces to AT as the fieldset group label |
| `<h3 aria-hidden="true">` | — | Visually visible; excluded from AT to avoid duplication |
| `<p>` (description) | paragraph | Read by AT as regular content; not programmatically linked to the fieldset |

---

## 3. Fieldset + legend grouping

AT announces the `<legend>` text when focus enters any field within the
`<fieldset>`. This satisfies WCAG 1.3.1 for related field grouping.

The dual-channel pattern (sr-only `<legend>` + `aria-hidden` `<h3>`) is the
correct approach when a visible heading is desired alongside AT-facing grouping.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 4.1.2 Name, Role, Value

---

## 4. Description text

The `description` `<p>` renders visible text below the heading. It is NOT
linked to the `<fieldset>` via `aria-describedby`. AT users hear it only when
they read linearly through the page — not when focusing into a child field.

This is acceptable for M1 (description text is context for the section heading,
not field-level metadata).

---

## 5. Keyboard navigation

FormSection does not modify keyboard navigation. Tab order within the section
follows DOM order of children.

**WCAG citations:**
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.1.2 No Keyboard Trap

---

## 6. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | Description `<p>` is not associated with the fieldset via `aria-describedby` | Low | Add `aria-describedby` to the `<fieldset>` pointing to the description paragraph (if needed for section-level context) |
