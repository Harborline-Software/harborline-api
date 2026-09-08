# PropertyCard — Semantic Contract

- **Component:** PropertyCard
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted (domain-tier — NOT @harborline-software/ui-react scope; see scope warning below)
- **Companion contracts:** [Interaction](./PropertyCard.Interaction.md) · [Accessibility](./PropertyCard.Accessibility.md) · [Styling](./PropertyCard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/PropertyCard.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — domain-specific composite card (no Radix primitive)

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

PropertyCard is a compact card component that displays a summary of a real property entity: address, city/state, unit count, status, and an optional name identifier. An optional `actions` slot renders action buttons in a footer zone.

PropertyCard is a **domain-aware display component** — its props mirror the Property domain entity directly, making it specific to the property management use case.

**Note:** Per the fleet's "No domain components in ui-react" memory entry, PropertyCard is an implementation-first component that exists in the library but should not be expanded with additional domain-specific logic. Future refactoring should consider whether PropertyCard belongs in a domain-specific package rather than `@harborline-software/ui-react`.

---

## 2. Data model

```typescript
interface PropertyCardProps {
  name: string
  address: string
  city: string
  state: string
  units: number
  status: 'Active' | 'Vacant' | 'Maintenance' | 'Sold' | string
  className?: string
  actions?: ReactNode
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `name` | `string` | _required_ | Property identifier (e.g., property code `"PROP-001"`). Rendered as monospace small text in the bottom row. |
| `address` | `string` | _required_ | Street address. Truncated with ellipsis if too long. |
| `city` | `string` | _required_ | City name. |
| `state` | `string` | _required_ | State/province code or name. |
| `units` | `number` | _required_ | Number of units. Renders as `"N unit"` / `"N units"` (pluralized). |
| `status` | `'Active' \| 'Vacant' \| 'Maintenance' \| 'Sold' \| string` | _required_ | Property status. Four known values have defined colour treatments; all others fall back to muted styling. |
| `className` | `string` | `''` | Additional classes on the card root. |
| `actions` | `ReactNode` | `undefined` | When provided, renders a bordered footer zone below the main card content. Typically `<button>` elements. |

### 3.1 Status semantics

| `status` | Colour treatment |
|---|---|
| `'Active'` | `bg-success/15 text-success` |
| `'Vacant'` | `bg-warning/15 text-warning` |
| `'Maintenance'` | `bg-priority-medium text-priority-medium-fg` |
| `'Sold'` | `bg-muted text-muted-foreground` |
| Any other string | `bg-muted text-muted-foreground` (fallback) |

### 3.2 Units pluralization

Built into the component: `{units} unit{units !== 1 ? 's' : ''}`. No external formatting needed.

---

## 4. Events

PropertyCard has **no events**. The `actions` slot carries all interactivity.

---

## 5. Slots

| Slot prop | Purpose |
|---|---|
| `actions` | Footer action zone. Renders after a top border separator when provided. Typically contains `<button>` or `<a>` elements. |

---

## 6. Component composition

- **Property list views.** Grid of PropertyCards; each card links to the property detail page.
- **Dashboard tiles.** Summary of property portfolio.
- **Assigned property widget.** Individual PropertyCard in a staff assignment panel.

---

## 7. Domain component notice

PropertyCard's props are 1-to-1 with the `Property` domain entity (address, city, state, units, status). This is intentional as an expedient for the v1 portfolio, but:

- PropertyCard MUST NOT gain additional domain entity fields (lease data, financial data, work orders).
- Future v2 refactoring should move PropertyCard to a domain-scoped package.
- The `actions` slot is the correct extension point; do not add action buttons as first-class props.

---

## 8. Deferred features

- **Image/thumbnail slot** — property photo in the card header. Deferred.
- **Metric row** — occupancy rate, rent total. Deferred.
- **Skeleton/loading state** — Deferred.
- **Selected/highlighted state** — for multi-select grid. Deferred.
