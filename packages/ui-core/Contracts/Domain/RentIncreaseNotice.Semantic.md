# RentIncreaseNotice — Semantic Contract

- **Component:** RentIncreaseNotice
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted (domain-tier — NOT @harborline-software/ui-react scope; see scope warning below)
- **Companion contracts:** [Interaction](./RentIncreaseNotice.Interaction.md) · [Accessibility](./RentIncreaseNotice.Accessibility.md) · [Styling](./RentIncreaseNotice.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/RentIncreaseNotice.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — domain-specific notice display

---
---

> **SCOPE WARNING (MG3-7):** This component is a **domain-tier component** specific to the Harborline ERP
> application domain. It MUST NOT be added to `@harborline-software/ui-react` (the shared component library).
> The `feedback_ui_react_no_domain_components` fleet rule explicitly prohibits property-management
> and building-systems domain components from the shared library (~500 PRs were closed/reverted
> when this boundary was violated). This spec is retained as app-domain documentation only.
> If implementation is needed, it belongs in the Harborline app layer, not in `packages/ui-react`.

---


## 1. Purpose

RentIncreaseNotice is a **structured notice card** that presents the details
of a formal rent increase notification. It surfaces the current rent, new
rent, effective date, notice status, and optional legal details (notice sent
date, notice period, reason). In actionable states it also renders tenant
response buttons (Acknowledge, Dispute).

This is a domain-specific display component — it should not be used as a
general notification or alert component.

---

## 2. Data model

```typescript
type RentIncreaseStatus = 'draft' | 'sent' | 'acknowledged' | 'disputed' | 'effective'

interface RentIncreaseNoticeProps {
  tenantName?: string
  unitAddress?: string
  currentRent: number
  newRent: number
  effectiveDate: string
  noticeSentDate?: string
  status?: RentIncreaseStatus
  noticePeriodDays?: number
  reason?: string
  currency?: string
  className?: string
  onAcknowledge?: () => void
  onDispute?: () => void
  onDownload?: () => void
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `currentRent` | `number` | _required_ | Current monthly rent amount. |
| `newRent` | `number` | _required_ | New monthly rent after increase. |
| `effectiveDate` | `string` | _required_ | Display-formatted date the new rent takes effect. |
| `tenantName` | `string` | — | Optional tenant name shown in header. |
| `unitAddress` | `string` | — | Optional unit address shown in header. |
| `noticeSentDate` | `string` | — | Display-formatted date the notice was sent. |
| `status` | `RentIncreaseStatus` | `'draft'` | Current lifecycle status of the notice. |
| `noticePeriodDays` | `number` | — | Legal notice period in days (e.g., 60). |
| `reason` | `string` | — | Explanation for the rent increase. |
| `currency` | `string` | `'USD'` | ISO 4217 currency code for formatting. |
| `className` | `string` | — | Additional classes on root container. |
| `onAcknowledge` | `() => void` | — | Tenant acknowledgment callback. Rendered only when `status='sent'`. |
| `onDispute` | `() => void` | — | Tenant dispute callback. Rendered only when `status='sent'`. |
| `onDownload` | `() => void` | — | Download PDF callback. Rendered when provided, regardless of status. |

### 3.1 Status lifecycle

| `status` | Label | Color | Actionable buttons |
| --- | --- | --- | --- |
| `draft` | Draft | Gray | None |
| `sent` | Sent | Blue | Acknowledge + Dispute (when callbacks provided) |
| `acknowledged` | Acknowledged | Emerald | None |
| `disputed` | Disputed | Red | None |
| `effective` | Effective | Purple | None |

### 3.2 Computed fields

- **Increase amount** = `newRent - currentRent`
- **Increase percent** = `(increase / currentRent) * 100` (1 decimal place)
- **Actionable** = `status === 'sent'`

### 3.3 Currency formatting

Uses `Intl.NumberFormat('en-US', { style: 'currency', currency, minimumFractionDigits: 2 })`.

---

## 4. Events

| Event | Trigger |
| --- | --- |
| `onAcknowledge()` | Acknowledge button click (only when `status='sent'`) |
| `onDispute()` | Dispute button click (only when `status='sent'`) |
| `onDownload()` | Download PDF button click (any status) |

---

## 5. Composition

### Landlord view — notice in sent state

```tsx
<RentIncreaseNotice
  tenantName="Jane Smith"
  unitAddress="Apt 4B, 123 Main St"
  currentRent={1500}
  newRent={1625}
  effectiveDate="August 1, 2024"
  noticeSentDate="May 28, 2024"
  status="sent"
  noticePeriodDays={60}
  reason="Market rate adjustment"
  onDownload={() => downloadNotice(noticeId)}
/>
```

### Tenant view — with acknowledge/dispute actions

```tsx
<RentIncreaseNotice
  currentRent={1500}
  newRent={1625}
  effectiveDate="August 1, 2024"
  status="sent"
  onAcknowledge={() => acknowledgeNotice(noticeId)}
  onDispute={() => openDisputeFlow(noticeId)}
/>
```

---

## 6. Deferred features

- **Multiple notices** — single-notice card only; no list/summary view.
- **Legal document attach** — no document slot.
- **History timeline** — no status change audit trail.
