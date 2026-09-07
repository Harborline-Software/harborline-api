# FreshnessBadge — Interaction Contract

- **Component:** FreshnessBadge
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FreshnessBadge.Semantic.md) · [Accessibility](./FreshnessBadge.Accessibility.md) · [Styling](./FreshnessBadge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/FreshnessBadge.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Display-only — no user interaction

FreshnessBadge is purely informational. It renders a relative time string
and optional stale indicator. There are no clickable elements and no events
fired by user interaction.

The `title` attribute on the container span provides an absolute timestamp
on hover via the native browser tooltip, but this is not an interactive
element.

---

## 2. Auto-tick requirement

**FreshnessBadge MUST register its own `setInterval` to keep the displayed label current.** Without a self-contained timer, the component shows the relative time as of the initial render and never updates — a component showing "3m ago" at mount would still show "3m ago" after 10 minutes, misleading users about data freshness.

The interval cadence MUST be tight enough to produce timely label transitions:

| Current age range | Interval |
| --- | --- |
| `< 60 s` | 5 s (updates second-granularity labels promptly) |
| `60 s – 59 min` | 30 s (label changes by the minute; 30s keeps it within ~30s of truth) |
| `≥ 60 min` | 60 s (label changes by the hour; minute cadence is sufficient) |

Implementation pattern (React):
```tsx
useEffect(() => {
  const id = setInterval(() => setNow(Date.now()), tickMs)
  return () => clearInterval(id)
}, [tickMs])
```

`tickMs` is derived from the current age range at mount; the interval can be re-registered when the label bucket changes (s → m → h) to avoid unnecessary renders at fine cadence after the first minute.

**Cleanup:** the interval MUST be cleared in the `useEffect` cleanup. Failure to clear causes memory leaks when FreshnessBadge is unmounted (e.g., when the parent route unmounts or the data panel is closed).

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
| --- | --- | --- | --- |
| G-FB4 | High | M1 implementation does NOT register an auto-tick interval — label is frozen at render time. Labeled "deferred" in Semantic §6 but is a functional correctness issue, not a feature. Any parent that omits its own tick will silently show stale relative-time labels. | **Must fix before v1 production use.** Implement per §2 above. |
