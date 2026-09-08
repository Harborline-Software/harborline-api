# Tooltip — Accessibility Contract

- **Component:** Tooltip
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Tooltip.Semantic.md) · [Interaction](./Tooltip.Interaction.md) · [Styling](./Tooltip.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Tooltip.tsx`
- **Catalog row:** #140 Tooltip (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="tooltip"` | Tooltip bubble `<span>` | Marks the element as a tooltip |

---

## 2. Association gap

The tooltip bubble has `role="tooltip"` but the trigger element is NOT automatically associated with it via `aria-describedby`. For AT to announce the tooltip content when the trigger is focused, the trigger needs:

```html
<button aria-describedby="tooltip-id">…</button>
<span role="tooltip" id="tooltip-id">Tooltip text</span>
```

This association is **not implemented in M1** — `id` assignment and `aria-describedby` wiring is host responsibility.

---

## 3. Focus-triggered display

The tooltip appears on `focus` (same delay as hover). Keyboard users who Tab to the trigger will see the tooltip after `delayDuration` ms.

---

## 4. Blocking constraint — do not use Tooltip as the sole information source

**WCAG 1.3.1 (Level A) violation risk:** Tooltip content MUST NOT be the only place where actionable or essential information is conveyed. AT users may never trigger hover; keyboard users who do not focus the trigger also never see the tooltip. If the information in the tooltip is necessary to understand or use the UI, it must also be conveyed through a visible label, help text, or other non-tooltip surface.

**Correct uses of Tooltip:**
- Labelling icon-only buttons (where an `aria-label` on the trigger also carries the same text)
- Supplementary description that is also present in context (e.g., a field label explains the field; the tooltip adds a usage example)
- Keyboard shortcut hints (decorative; not required to operate the control)

**Incorrect uses of Tooltip:**
- Putting required field instructions only in a tooltip (use a `<FormField>` `description` prop instead)
- Putting validation error messages only in a tooltip (use `ValidationMessage` component instead)
- Putting navigation destination only in a tooltip on a link (use visible link text or `aria-label` on the anchor)

The trigger element MUST have an accessible name independent of the tooltip. A tooltip is a supplemental description, not a primary label.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TT4 | High | No automatic `aria-describedby` wiring between trigger and tooltip | Blocking-before-v1-ship — WCAG SC 4.1.2 (Level A) violation; must resolve before v1 ship |
| G-TT5 | Medium | Tooltip not persistent — disappears on blur, preventing AT from reading it in some cases | Accepted-risk M1; `role="tooltip"` is correct; AT reads it on focus trigger |
