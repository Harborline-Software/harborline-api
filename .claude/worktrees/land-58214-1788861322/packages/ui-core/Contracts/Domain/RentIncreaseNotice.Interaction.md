# RentIncreaseNotice — Interaction Contract

- **Component:** RentIncreaseNotice
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RentIncreaseNotice.Semantic.md) · [Accessibility](./RentIncreaseNotice.Accessibility.md) · [Styling](./RentIncreaseNotice.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/RentIncreaseNotice.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Action buttons

Action buttons are rendered only when `(isActionable || onDownload)`:
`isActionable = status === 'sent'`.

| Button | Condition | Trigger | Effect |
| --- | --- | --- | --- |
| Download PDF | `onDownload` provided (any status) | Click | `onDownload()` |
| Acknowledge | `status='sent'` AND `onAcknowledge` provided | Click | `onAcknowledge()` |
| Dispute | `status='sent'` AND `onDispute` provided | Click | `onDispute()` |

---

## 2. No state management

RentIncreaseNotice is fully controlled — it renders based on the `status`
prop and fires callbacks without changing its own state. Status transitions
(e.g., sent → acknowledged) are the host's responsibility.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-RIN1 | Low | No loading/disabled state on Acknowledge or Dispute buttons — double-click risk | Accepted-risk M1 |
| G-RIN2 | Low | No confirmation step before Acknowledge/Dispute — actions are single-click with no undo | Accepted-risk M1 |
