# ConnectionStatus — Semantic Contract

- **Component:** ConnectionStatus
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ConnectionStatus.Interaction.md) · [Accessibility](./ConnectionStatus.Accessibility.md) · [Styling](./ConnectionStatus.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ConnectionStatus.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled status indicator

---

## 1. Purpose

ConnectionStatus is an **inline banner that surfaces network connectivity
state** to the user. It renders only when the connection is not `online`;
in the `online` state it renders `null` (silent). It supports both
host-controlled state and browser-native auto-detection via `navigator.onLine`.

ConnectionStatus vs OfflineIndicator:

| Criterion | ConnectionStatus | OfflineIndicator |
| --- | --- | --- |
| States | `online` / `offline` / `reconnecting` | Binary offline only |
| Auto-detect | Optional (`autoDetect`) | Always auto-detects |
| Retry action | Optional (`onReconnect`) | None |
| Offline message | Sync-on-reconnect hint | Configurable `message` prop |

---

## 2. Data model

```typescript
type ConnectionState = 'online' | 'offline' | 'reconnecting'

interface ConnectionStatusProps {
  state?: ConnectionState
  autoDetect?: boolean
  onReconnect?: () => void
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `state` | `ConnectionState` | — | Explicit state override. When absent and `autoDetect=false`, defaults to `'online'` (renders nothing). |
| `autoDetect` | `boolean` | `false` | When true, subscribes to `window.online`/`offline` events and derives state automatically. `propState` overrides if also provided. |
| `onReconnect` | `() => void` | — | When provided and `state='offline'`, renders a Retry button. Host triggers the reconnect attempt. |
| `className` | `string` | `''` | Additional classes on the container `<div>`. |

### 3.1 State semantics

| `state` | Semantic | Label | Renders |
| --- | --- | --- | --- |
| `online` | Connected | — | Nothing (returns `null`) |
| `offline` | No internet | "No internet connection" | Banner + optional Retry |
| `reconnecting` | Attempting reconnect | "Reconnecting…" | Banner (no Retry — reconnect in progress) |

### 3.2 State resolution

Effective state = `propState ?? (autoDetect ? autoState : 'online')`.

When `autoDetect=true` and no `state` prop is provided, the component drives
itself from `navigator.onLine` + event listeners. When `state` is provided,
it always wins regardless of `autoDetect`.

---

## 4. Events

| Event | Signature | Trigger |
| --- | --- | --- |
| `onReconnect` | `() => void` | Retry button clicked (only when `state='offline'` and `onReconnect` is provided). |

---

## 5. Composition

### Auto-detect mode (no explicit state)

```tsx
<ConnectionStatus autoDetect onReconnect={() => retryConnection()} />
```

### Host-controlled state (e.g., WebSocket disconnect)

```tsx
<ConnectionStatus
  state={wsState === 'disconnected' ? 'offline' : wsState === 'connecting' ? 'reconnecting' : 'online'}
  onReconnect={() => ws.reconnect()}
/>
```

---

## 6. Deferred features

- **Reconnecting animation** — the `reconnecting` state shows a pulsing dot
  but no progress indicator or timeout countdown.
- **`reconnecting` Retry button** — Retry is intentionally hidden when
  `reconnecting` (reconnect already in progress); an abort/cancel button is
  not supported.
- **Custom labels** — state labels are hardcoded; no `labels` prop.
