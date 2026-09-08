# EmptyState — Semantic Contract

- **Component:** EmptyState
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./EmptyState.Interaction.md) · [Accessibility](./EmptyState.Accessibility.md) · [Styling](./EmptyState.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/EmptyState.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled placeholder layout `<div>` wrapper

---

## ⚠ Duplicate — see canonical at DataDisplay/EmptyState.*

This contract family is a duplicate of `DataDisplay/EmptyState.*`, which is the
**canonical set** (catalog entry A18). Both reference the same implementation at
`packages/ui-react/src/components/datagrid/EmptyState.tsx`. Accessibility specs
diverge: DataDisplay recommends `role="status"` when composed inside stateful
hosts (DataGrid); this Feedback version says no live region is needed.

**Resolution:** The DataDisplay family is authoritative. The Feedback family
should be treated as a draft-in-error. The Feedback Accessibility divergence
is a known gap — the `role="status"` wrap-pattern documented in
`DataDisplay/EmptyState.Accessibility.md §6` applies to both use cases.

---

## 1. Purpose

EmptyState is a **centered placeholder displayed when a data container has
no records to show**. It is used inside datagrid containers, list panels, and
search results when the result set is empty. Three variants communicate
different empty-state reasons and guide the user toward the appropriate
next action.

---

## 2. Data model

```typescript
type EmptyStateVariant = 'informational' | 'positive' | 'actionable'

interface EmptyStateAction {
  label: string
  onClick: () => void
}

interface EmptyStateProps {
  variant: EmptyStateVariant
  title: string
  description?: string
  action?: EmptyStateAction
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `variant` | `'informational' \| 'positive' \| 'actionable'` | _required_ | Determines icon and semantic tone of the empty state. |
| `title` | `string` | _required_ | Primary heading. Describes why the list is empty. |
| `description` | `string` | — | Optional supporting text with more context or guidance. |
| `action` | `EmptyStateAction` | — | Optional single call-to-action button. |

### 3.1 Variant semantics

| `variant` | Semantic intent | Icon | Typical use |
| --- | --- | --- | --- |
| `informational` | Neutral — no data available | Info circle (gray) | "No records found" after a filter |
| `positive` | Positive — clean slate | Check circle (green) | "All caught up" / no overdue items |
| `actionable` | Invitation to create first record | Plus circle (gray) | "No vendors yet — add one" |

### 3.2 `action` slot

When `action` is provided, a button is rendered below `description`. The
button's label and click handler are fully host-controlled. Typically used
with `actionable` variant to offer a "Create first X" CTA.

---

## 4. Events

| Event | Signature | Trigger |
| --- | --- | --- |
| `action.onClick` | `() => void` | User clicks the action button. |

---

## 5. Composition

### No records after search

```tsx
<EmptyState
  variant="informational"
  title="No vendors found"
  description="Try adjusting your search filters."
/>
```

### All items resolved

```tsx
<EmptyState
  variant="positive"
  title="All work orders complete"
  description="There are no open work orders for this property."
/>
```

### First-run empty list

```tsx
<EmptyState
  variant="actionable"
  title="No tenants yet"
  description="Add your first tenant to get started."
  action={{ label: 'Add tenant', onClick: () => navigate('/tenants/new') }}
/>
```

---

## 6. Deferred features

- **Custom icon** — icons are hardcoded per variant; no `icon` prop override.
- **Illustration slot** — no support for larger artwork or SVG illustrations.
- **Multiple actions** — single `action` object; multi-button CTAs not
  supported.
