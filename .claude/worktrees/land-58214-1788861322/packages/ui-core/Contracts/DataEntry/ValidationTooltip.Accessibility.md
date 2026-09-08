# ValidationTooltip — Accessibility Contract

- **Component:** ValidationTooltip
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ValidationTooltip.Semantic.md) · [Interaction](./ValidationTooltip.Interaction.md) · [Styling](./ValidationTooltip.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #147 ValidationTooltip (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik ValidationTooltip baseline)

---

## 1. ARIA role

`role="tooltip"` on the tooltip element.

---

## 2. Association with input

The associated input should have `aria-describedby="{tooltipId}"` pointing to the ValidationTooltip element's id. This ensures screen readers announce the validation message when the input is focused.

---

## 3. Live region

Additionally, the tooltip container should be an `aria-live="assertive"` region — validation errors fire on blur and must be announced immediately.

---

## 4. Known gaps

None identified for forward-spec.
