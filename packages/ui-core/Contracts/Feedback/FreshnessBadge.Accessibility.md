# FreshnessBadge — Accessibility Contract

- **Component:** FreshnessBadge
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FreshnessBadge.Semantic.md) · [Interaction](./FreshnessBadge.Interaction.md) · [Styling](./FreshnessBadge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/FreshnessBadge.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA roles and attributes

| Attribute | Element | Value |
| --- | --- | --- |
| `aria-hidden="true"` | Warning triangle SVG | Icon is decorative |
| `title` | Outer `<span>` | Absolute timestamp (browser tooltip) |
| `aria-live="polite"` | Inner label `<span>` | Announces relative-time updates to AT (polite = non-interruptive) |
| `aria-atomic="true"` | Inner label `<span>` | Ensures AT reads the full updated label, not just changed characters |

No explicit ARIA roles. The component is a passive `<span>` with visible
text content.

---

## 2. Live region guidance

The auto-tick interval (see Interaction §2) updates the displayed label every 5–60 seconds depending on age. Without `aria-live`, AT users never hear the label change — they only hear the value at the time focus passes over the element.

The inner `<span>` that renders the relative time string (not the outer container) MUST carry `aria-live="polite" aria-atomic="true"`:
- `polite` — updates queue after any ongoing AT speech, so they don't interrupt the user
- `atomic` — when the label changes from "3m ago" to "4m ago", AT reads the full new label, not just the delta

**Stale transition announcement:** When the component transitions from fresh to stale, the warning icon appears. Because the icon is `aria-hidden`, the only AT signal is the label update (live region fires). This is sufficient — the stale state is conveyed by the label update itself.

**Frequency note:** At < 60 s cadence (5 s tick), `aria-live` will announce every 5 seconds. This may be disruptive in AT-only contexts. Implementors should consider: do not mount FreshnessBadge in a focused region when seconds-level freshness is required. The `aria-live` is most valuable for the minutes→stale transition, not the per-second tick.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-FB1 | Low | No `aria-label` with the full absolute timestamp — screen readers read only the relative string (e.g., "3m ago") without the full date | Accepted-risk M1; `title` provides the absolute date for sighted users on hover |
| G-FB2 | Low | `title` attribute is not accessible to keyboard-only users (no focus target) | Accepted-risk M1 |
| G-FB3 | Medium | 5 s `aria-live` tick cadence may be disruptive to AT users in focused reading contexts | Accepted-risk M1; host should avoid mounting FreshnessBadge near focused AT reading areas when using seconds-level freshness |
