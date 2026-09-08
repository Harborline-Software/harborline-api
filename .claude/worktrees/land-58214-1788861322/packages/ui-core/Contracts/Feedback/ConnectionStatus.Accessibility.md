# ConnectionStatus — Accessibility Contract

- **Component:** ConnectionStatus
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ConnectionStatus.Semantic.md) · [Interaction](./ConnectionStatus.Interaction.md) · [Styling](./ConnectionStatus.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/ConnectionStatus.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `role="alert"` | Container `<div>` | Live region for AT announcements — implicit `aria-live="assertive"` |
| `aria-hidden="true"` | Status dot `<span>` | Decorative indicator dot |

**Forbidden:** `role="status" aria-live="assertive"` on the same element — this is a
dual-source conflict. `role="status"` carries implicit `aria-live="polite"`;
the explicit `assertive` overrides that inconsistently across AT (JAWS/NVDA may
double-announce or ignore one value). See `Notification.Accessibility.md §2`
for the fleet-wide rule: never pair a role with a conflicting explicit aria-live.

---

## 2. Live region behavior

The component renders `null` when online — the live region is dynamically **inserted**
into the DOM precisely when the connection degrades. This is the correct pattern for
`role="alert"`: the role fires when the element is dynamically injected (or its text
content changes), not on initial render.

When the component mounts with `status="offline"`, AT immediately announces the
`label` text. This is the intended behaviour for connection loss (urgent, assertive
announcement).

**Role selection rationale:**
- `role="alert"` → implicit `aria-live="assertive"` → interrupts AT immediately. ✓ CORRECT
- `role="status"` → implicit `aria-live="polite"` → wrong urgency for connection loss.
- `role="status" + aria-live="assertive"` → contradictory; M1 anti-pattern. ✗ FORBIDDEN

**WCAG citations:** SC 4.1.3 Status Messages; WAI-ARIA 1.2 `alert` role.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-CS3 | High | M1 implementation used `role="status" aria-live="assertive"` — dual-source conflict that double-announces or behaves inconsistently across JAWS/NVDA. | [RESOLVED 2026-06-06] §1: spec now requires `role="alert"` only; M1 impl must remove `aria-live="assertive"` |
| G-CS4 | Low | No `aria-label` on the Retry button beyond its visible text "Retry" — acceptable for this brief label | Accepted-risk M1 |
| G-CS5 | Low | State transitions (offline → reconnecting → online) may not be re-announced if the component re-renders without unmounting | Accepted-risk M1 |
