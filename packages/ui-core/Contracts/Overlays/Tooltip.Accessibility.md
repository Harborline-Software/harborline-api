# Tooltip — Accessibility Contract

- **Component:** Tooltip
- **ADR 0017 family:** Overlays
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Tooltip.Semantic.md) · [Interaction](./Tooltip.Interaction.md) · [Styling](./Tooltip.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Tooltip.tsx`
- **Catalog row:** #140 Tooltip (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="tooltip"` | Tooltip label `<span>` | WAI-ARIA tooltip role |

---

## 2. Trigger accessibility

The outer wrapper `<span>` has `onFocus` and `onBlur` handlers, so keyboard focus on the child element triggers the tooltip. The WAI-ARIA pattern requires the trigger to have `aria-describedby` pointing to the tooltip element.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-TT4 | High | Trigger element has no `aria-describedby` linking it to the `role="tooltip"` span — AT will not automatically announce the tooltip on focus | Accepted-risk M1 |
| G-TT5 | Medium | Tooltip is conditionally rendered (`{visible && ...}`) — tooltip ID is not stable for `aria-describedby` | Accepted-risk M1; paired with G-TT4 |
| G-TT6 | Medium | Escape key does not dismiss tooltip (see G-TT1) | Accepted-risk M1 |

## 4. Blocking note

G-TT4 and G-TT5 are co-dependent and must be fixed together before production use. G-TT5 (unstable tooltip id due to conditional rendering) is the root blocker for G-TT4 (no `aria-describedby` on trigger). Fix: always render the tooltip `<span>` in the DOM with `visibility: hidden` (not conditional mount), assign a stable `id`, and set `aria-describedby={tooltipId}` on the trigger. A tooltip that AT cannot read is hover-only decoration, which fails the component's own purpose for informational content.
