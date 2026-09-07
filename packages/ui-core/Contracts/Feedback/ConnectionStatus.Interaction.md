# ConnectionStatus — Interaction Contract

- **Component:** ConnectionStatus
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ConnectionStatus.Semantic.md) · [Accessibility](./ConnectionStatus.Accessibility.md) · [Styling](./ConnectionStatus.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ConnectionStatus.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Retry button

| Condition | Trigger | Effect |
| --- | --- | --- |
| `state='offline'` AND `onReconnect` provided | Retry button click | `onReconnect()` |
| `state='reconnecting'` | — | No Retry button rendered |
| `state='online'` | — | Component renders `null` |

The Retry button is a `<button type="button">`. Activation fires the
host-supplied `onReconnect` callback. The component does not change its
own state on Retry — the host drives state based on the result.

---

## 2. Auto-detect lifecycle

When `autoDetect=true`:

| Browser event | Effect |
| --- | --- |
| `window` `online` | `autoState → 'online'` (component renders `null`) |
| `window` `offline` | `autoState → 'offline'` (banner renders) |
| Component unmount | Event listeners removed |

Auto-detect sets the initial state from `navigator.onLine` on mount.

---

## 3. No user-dismissal

ConnectionStatus does not render a close/dismiss button. It renders as long
as `state !== 'online'`. The host removes it by changing the state (or the
browser goes back online in `autoDetect` mode).

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-CS1 | Low | `autoDetect` does not model the `reconnecting` state automatically — only `online`/`offline` are derived from browser events | Accepted-risk M1 |
| G-CS2 | Low | No debounce on `online`/`offline` events — rapid transitions may cause flicker | Accepted-risk M1 |
