# FreshnessBadge — Semantic Contract

- **Component:** FreshnessBadge
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./FreshnessBadge.Interaction.md) · [Accessibility](./FreshnessBadge.Accessibility.md) · [Styling](./FreshnessBadge.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/FreshnessBadge.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled freshness indicator badge

---

## 1. Purpose

FreshnessBadge displays **how long ago data was last updated** as a relative
time string (e.g., "3m ago", "1h ago"). It also signals when the data is
considered stale by switching to a warning color and rendering a warning
triangle icon. Intended as a small inline indicator adjacent to data panels
or table headers.

---

## 2. Data model

```typescript
interface FreshnessBadgeProps {
  updatedAt: Date | string | number
  staleAfterMs?: number
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `updatedAt` | `Date \| string \| number` | _required_ | The timestamp of the last data update. Accepts a `Date` object, ISO string, or Unix ms timestamp. |
| `staleAfterMs` | `number` | `300_000` (5 min) | Milliseconds after which the data is considered stale. When `age > staleAfterMs`, the warning state is activated. |
| `className` | `string` | `''` | Additional classes on the outer `<span>`. |

### 3.1 States

| State | Condition | Label example | Icon |
| --- | --- | --- | --- |
| Fresh | `age ≤ staleAfterMs` | "3m ago" | None |
| Stale | `age > staleAfterMs` | "12m ago" | Warning triangle |

### 3.2 Relative time format

| Age | Format |
| --- | --- |
| < 60 s | `{n}s ago` |
| 60 s – 59 min | `{n}m ago` |
| ≥ 60 min | `{n}h ago` |

### 3.3 Client-only rendering

FreshnessBadge renders `null` on the server (or before hydration): a
`mounted` state is set via `useEffect`. This prevents server/client timestamp
mismatch hydration errors.

### 3.4 `title` attribute

The outer `<span>` renders `title={new Date(updatedAt).toLocaleString()}` —
hovering shows the absolute timestamp in a browser tooltip.

---

## 4. Events

Display-only — no user interaction. No events fired.

---

## 5. Composition

```tsx
<div className="flex items-center gap-2">
  <span className="text-sm font-medium">Rent roll</span>
  <FreshnessBadge updatedAt={lastSyncedAt} staleAfterMs={10 * 60 * 1000} />
</div>
```

---

## 6. Auto-tick (required, not deferred)

FreshnessBadge MUST self-tick to keep the relative time label current. See
FreshnessBadge.Interaction §2 for the interval cadence spec. The M1
implementation does not yet include the tick (tracked as G-FB4 in
FreshnessBadge.Interaction §3) — this is a correctness bug, not a deferred
feature.

---

## 7. Deferred features

- **Custom format** — relative time format is hardcoded; no `formatRelative`
  prop.
- **Stale threshold tooltip** — no built-in explanation of why data is stale.
