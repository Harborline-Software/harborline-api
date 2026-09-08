# OfflineIndicator — Accessibility Contract

- **Component:** OfflineIndicator
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./OfflineIndicator.Semantic.md) · [Interaction](./OfflineIndicator.Interaction.md) · [Styling](./OfflineIndicator.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/OfflineIndicator.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `role="alert"` | Container `<div>` | Assertive live region — AT announces immediately on mount |
| `aria-hidden="true"` | Warning triangle SVG | Decorative icon |

---

## 2. Live region behavior

`role="alert"` triggers assertive announcement when the component mounts
(i.e., when the device goes offline and the banner appears in the DOM).
This is appropriate — losing connectivity is a critical state the user must
be informed of immediately.

---

## 3. Reconnection announcement

When `window.online` fires and `reconnectFlashMs > 0` (the default), the component re-renders with the `reconnectMessage` prop and `role="status"` (polite). This gives AT a non-interruptive "back online" announcement before the component unmounts.

The `role` switch from `role="alert"` (offline) to `role="status"` (reconnected) is intentional: going offline is critical (assertive), coming back online is informational (polite).

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-OI1 | Low | Re-connect (offline → online) removes the element from DOM — no "back online" announcement | [RESOLVED 2026-06-06] §3 + Interaction §3: reconnect flash with `role="status"` now spec'd |
