# RentIncreaseNotice — Accessibility Contract

- **Component:** RentIncreaseNotice
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RentIncreaseNotice.Semantic.md) · [Interaction](./RentIncreaseNotice.Interaction.md) · [Styling](./RentIncreaseNotice.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/RentIncreaseNotice.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `<dl>` | Details grid | Semantic description list for key/value pairs (effective date, notice sent, etc.) |

No explicit ARIA live regions — RentIncreaseNotice is a static display card,
not a dynamically-updating status component.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-RIN3 | Medium | The percentage increase pill (e.g., "+8.3%") is purely visual with no accessible label describing it as a percentage increase | Accepted-risk M1 |
| G-RIN4 | Low | Currency amounts (current/new rent) are rendered as formatted strings only — AT may read them character by character without semantic monetary context | Accepted-risk M1 |
| G-RIN5 | Low | Action buttons (Acknowledge, Dispute) have no `aria-describedby` referencing the notice title for additional context | Accepted-risk M1 |
