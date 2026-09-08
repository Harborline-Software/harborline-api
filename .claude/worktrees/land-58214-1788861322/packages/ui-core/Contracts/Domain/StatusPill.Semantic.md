# StatusPill — Semantic Contract

- **Component:** StatusPill
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted (domain-tier — NOT @harborline-software/ui-react scope; see scope warning below)
- **Companion contracts:** [Interaction](./StatusPill.Interaction.md) · [Accessibility](./StatusPill.Accessibility.md) · [Styling](./StatusPill.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/StatusPill.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled status badge `<span>`

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

StatusPill renders a **domain-colored pill badge** for specific Harborline ERP
status values. Unlike a generic badge, it is keyed to a `kind` (entity type)
and `value` (status string), resolving the appropriate color from a hardcoded
per-kind color map. It is the primary status indicator in Harborline datagrid
cells.

StatusPill is intentionally domain-specific — it directly maps Harborline entity
status strings to design-system colors. It does NOT accept arbitrary color
overrides.

---

## 2. Data model

```typescript
type StatusPillKind =
  | 'glAccountType'
  | 'occupancyStatus'
  | 'agingBucket'
  | 'balanceState'
  | 'workOrderStatus'

interface StatusPillProps {
  kind: StatusPillKind
  value: string
  tooltip?: string
  outlined?: boolean
  leadingIcon?: React.ReactNode   // optional icon for non-color differentiation; see StatusPill.Accessibility §3
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `kind` | `StatusPillKind` | _required_ | Entity type determining which color map to use. |
| `value` | `string` | _required_ | Status string key within the kind's color map. Falls back to gray when not found. |
| `tooltip` | `string` | — | Optional browser `title` tooltip. |
| `outlined` | `boolean` | `false` | When true and the color map entry has no border, adds `border border-current`. |

### 3.1 Kind → value maps

#### `glAccountType`

| Value | Color |
| --- | --- |
| `Asset` | Blue |
| `Liability` | Purple |
| `Equity` | Slate |
| `Revenue` | Green |
| `Expense` | Amber |

#### `occupancyStatus`

| Value | Color |
| --- | --- |
| `Occupied` | Green |
| `NoticeGiven` | Amber |
| `Vacant` | Gray |
| `OffMarket` | Gray (outlined) |

#### `agingBucket`

| Value | Color |
| --- | --- |
| `NoBalance` | Gray-50 |
| `Current` | Gray |
| `Days0To30` | Yellow |
| `Days31To60` | Orange |
| `Days61To90` | Orange-dark |
| `Days90Plus` | Red |

#### `balanceState`

| Value | Color |
| --- | --- |
| `Balanced` | Green |
| `OutOfBalance` | Red |

#### `workOrderStatus`

| Value | Color |
| --- | --- |
| `Draft` | Blue |
| `Sent` | Purple |
| `Accepted` | Indigo |
| `Scheduled` | Yellow |
| `InProgress` | Orange |
| `Completed` | Green |
| `OnHold` | Gray |
| `Cancelled` | Red |

### 3.2 Fallback

Unknown `value` → `bg-gray-100 text-gray-700` (gray fallback).

---

## 4. Events

Display-only — no events.

---

## 5. Composition

### Datagrid occupancy status cell

```tsx
<StatusPill kind="occupancyStatus" value={unit.occupancyStatus} />
```

### Aging bucket with tooltip

```tsx
<StatusPill
  kind="agingBucket"
  value={tenant.agingBucket}
  tooltip={`${tenant.daysPastDue} days past due`}
/>
```

---

## 6. Deferred features

- **Custom kinds** — only the 5 hardcoded kinds are supported; no plugin/extension.
- **Icon** — no icon in pill.
- **Size variants** — single size only.
