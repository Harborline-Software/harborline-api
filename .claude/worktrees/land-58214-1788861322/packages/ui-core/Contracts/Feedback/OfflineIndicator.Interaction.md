# OfflineIndicator — Interaction Contract

- **Component:** OfflineIndicator
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./OfflineIndicator.Semantic.md) · [Accessibility](./OfflineIndicator.Accessibility.md) · [Styling](./OfflineIndicator.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/OfflineIndicator.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Display-only — no user interaction

OfflineIndicator is purely informational. It auto-shows when offline and
auto-hides when online. There are no buttons or interactive elements.

---

## 2. Auto-detect lifecycle

| Browser event | Effect |
| --- | --- |
| `window` `offline` | `offline → true`; banner renders; `role="alert"` announces immediately |
| `window` `online` | `offline → false`; component renders `null` after reconnection delay (see §3) |
| Component unmount | Event listeners removed |
| SSR / server render | `navigator.onLine` not available — renders `null` until `useEffect` hydrates |

---

## 3. Reconnection behavior

When `window` `online` fires, the component does **not** immediately unmount. The reconnection sequence is:

1. `offline` state transitions to `false`
2. A brief **"reconnected" flash** is shown for `reconnectFlashMs` (default: `2000 ms`) — the same banner container re-renders with a success message (e.g., "You're back online.") and `role="status"` (polite) instead of `role="alert"`
3. After `reconnectFlashMs`, the component renders `null`

This gives AT users a polite "back online" announcement (step 2) without the assertive interruption used for the offline announcement.

**Props added for reconnection:**

```typescript
interface OfflineIndicatorProps {
  message?: string              // offline message (default: "You are offline. Changes will sync when reconnected.")
  reconnectMessage?: string     // reconnected flash message (default: "You're back online.")
  reconnectFlashMs?: number     // duration to show reconnect flash before hiding (default: 2000)
  className?: string
}
```

**If `reconnectFlashMs=0`:** component unmounts immediately on `online` (no flash, no AT announcement for reconnection). Use this when the reconnection event is announced by a parent notification system.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-OI2 | Low | M1 implementation does not include the reconnect flash — it renders `null` immediately on `window.online`. | Must fix before v1 production use per this spec (§3). Tracked via G-OI1 in Accessibility. |
