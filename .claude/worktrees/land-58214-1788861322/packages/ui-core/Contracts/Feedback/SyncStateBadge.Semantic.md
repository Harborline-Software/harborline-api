# SyncStateBadge — Semantic Contract

- **Component:** SyncStateBadge
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./SyncStateBadge.Interaction.md) · [Accessibility](./SyncStateBadge.Accessibility.md) · [Styling](./SyncStateBadge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/SyncStateBadge.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled sync state indicator

---

## 1. Purpose

SyncStateBadge is a **compact inline indicator** of the local data
synchronization lifecycle for a Harborline offline-first feature. It shows a
colored dot and a label for one of five sync states. Typically placed in
panel headers, toolbar areas, or status bars.

The `synced` token is retained for compatibility. It means the configured local replication
exchange completed; it does not establish that the displayed record is globally current. Default
copy therefore says “Local changes exchanged,” never bare “Synced.”

SyncStateBadge vs ConnectionStatus / OfflineIndicator:

| Criterion | SyncStateBadge | ConnectionStatus / OfflineIndicator |
| --- | --- | --- |
| Focus | Data sync state | Network connectivity |
| Always visible | Can be always visible (incl. synced) | Renders `null` when online |
| States | 5 sync states | 2–3 connectivity states |
| Auto-detects | No — host controls state | Yes (optional) |

---

## 2. Data model

```typescript
type SyncState = 'synced' | 'syncing' | 'pending' | 'error' | 'offline'

interface SyncStateBadgeProps {
  state: SyncState
  label?: ReactNode
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `state` | `SyncState` | _required_ | Current sync state. Determines dot color and default label. |
| `label` | `ReactNode` | — | Custom label. When absent, uses the default label for the state. |
| `className` | `string` | `''` | Additional classes on the outer `<span>`. |

### 3.1 States and defaults

| `state` | Default label | Dot | Animation |
| --- | --- | --- | --- |
| `synced` | "Local changes exchanged" | `bg-success` | None |
| `syncing` | "Syncing…" | `bg-primary` | `animate-pulse` |
| `pending` | "Pending" | `bg-warning` | None |
| `error` | "Error" | `bg-destructive` | None |
| `offline` | "Offline" | `bg-status-offline` | None |

### 3.2 Custom label

The `label` prop accepts `ReactNode`, allowing custom text or inline
elements (e.g., "Last peer exchange 3m ago; currentness not established"). When provided, it replaces the
default label entirely.

---

## 4. Events

Display-only — no events.

---

## 5. Composition

### In a panel header

```tsx
<div className="flex items-center justify-between">
  <h2>Rent Roll</h2>
  <SyncStateBadge state={syncState} />
</div>
```

### With custom label

```tsx
<SyncStateBadge state="synced" label={<>Local changes exchanged · <FreshnessBadge updatedAt={lastSync} /></>} />
```

---

## 6. Deferred features

- **Click-to-retry** — in the `error` state, no built-in retry action.
- **Tooltip with last-sync time** — no built-in timestamp tooltip.
