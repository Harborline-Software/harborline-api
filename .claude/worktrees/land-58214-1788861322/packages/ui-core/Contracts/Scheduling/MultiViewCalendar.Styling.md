# MultiViewCalendar — Styling Contract

- **Component:** MultiViewCalendar
- **ADR 0017 family:** Scheduling
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./MultiViewCalendar.Semantic.md) · [Interaction](./MultiViewCalendar.Interaction.md) · [Accessibility](./MultiViewCalendar.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #113 MultiViewCalendar (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik MultiViewCalendar baseline)

---

## 1. Layout

`inline-flex gap-4` wrapping `views` Calendar month grids. A single shared header row spans the full width. Each Calendar pane inherits Calendar.Styling.md tokens.

---

## 2. Range band across months

Cross-month range highlighting: days between `start` and end of the left month, and days between start of the right month and `end`, both render `bg-accent/50`. Month-boundary cells use rounded-none to create a continuous band.

---

## 3. Design tokens

Same as Calendar.Styling.md §5.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-3 (appearance axes). Inherits Calendar Wave-N Styling expansion §6–§8.
> Supersedes: nothing — additive.

### 4. Year / decade view grids

Each pane renders its own year/decade grid following Calendar Wave-N Styling §6. The header row spans the full multi-pane width for year/decade views (not split per-pane) to reduce visual noise.

### 5. FR-3 appearance axes

MultiViewCalendar inherits the `size` axis from Calendar Wave-N Styling §7. The outer `inline-flex gap-4` wrapper scales its gap:

| size | Gap |
|---|---|
| `sm` | `gap-2` |
| `md` | `gap-4` |
| `lg` | `gap-6` |

`fillMode` and `rounded` do not apply (same rationale as Calendar).

### 6. activeRangeEnd visual indicator

When `activeRangeEnd='start'`, the start input/trigger receives a `ring-primary` emphasis ring. When `activeRangeEnd='end'`, the emphasis moves to the end input/trigger. Token: `ring-2 ring-primary`. This is a styling hint only — the actual ring rendering is the host's responsibility; this contract specifies the token to use.
