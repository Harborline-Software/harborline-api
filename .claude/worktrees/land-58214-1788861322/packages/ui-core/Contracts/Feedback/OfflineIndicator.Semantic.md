# OfflineIndicator — Semantic Contract

- **Component:** OfflineIndicator
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./OfflineIndicator.Interaction.md) · [Accessibility](./OfflineIndicator.Accessibility.md) · [Styling](./OfflineIndicator.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/OfflineIndicator.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled online/offline status display

---

## 1. Purpose

OfflineIndicator is a **simple always-auto-detecting offline banner** that
renders only when the device is offline. It shows a warning icon and a
configurable message. It renders `null` when online.

OfflineIndicator vs ConnectionStatus:

| Criterion | OfflineIndicator | ConnectionStatus |
| --- | --- | --- |
| State detection | Always auto-detects | Optional (`autoDetect`) |
| States modeled | Binary offline/online | `online` / `offline` / `reconnecting` |
| Retry action | None | Optional (`onReconnect`) |
| Message | Configurable string | Hardcoded per state |

---

## 2. Data model

```typescript
interface OfflineIndicatorProps {
  message?: string
  reconnectMessage?: string
  reconnectFlashMs?: number
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `message` | `string` | `'You are offline. Changes will sync when reconnected.'` | The message displayed to the user when offline. |
| `reconnectMessage` | `string` | `"You're back online."` | Message shown briefly when connectivity is restored (see Interaction §3). |
| `reconnectFlashMs` | `number` | `2000` | Duration in ms to show the reconnect flash before hiding. Pass `0` to skip the flash entirely. |
| `className` | `string` | `''` | Additional classes on the container `<div>`. |

### 3.1 Auto-detection

Uses `navigator.onLine` on mount for initial state, then subscribes to
`window.online` and `window.offline` events. No polling — purely event-driven.
Cleans up listeners on unmount.

---

## 4. Events

Display-only — no events fired.

---

## 5. Composition

### Global layout offline banner

```tsx
<main>
  <OfflineIndicator />
  <PageContent />
</main>
```

### Custom message

```tsx
<OfflineIndicator message="No connection. Your last sync was 5 minutes ago." />
```

---

## 6. Deferred features

- **Retry / reconnect action** — no built-in button. Use ConnectionStatus
  if a retry button is needed.
- **Icon override** — warning triangle is hardcoded.
