# Calendar — Styling Contract

- **Component:** Calendar
- **ADR 0017 family:** Scheduling
- **Contract type:** Styling
- **Polish reference:** modern picker grids (shadcn Calendar / SVAR) — see _shared/design/polish/2026-06-11-pilot-audit.md (pinned 2026-06-11, Polish-pilot — see _shared/design/polish-gate.md)
- **Polish reference (primary, KendoReact):** https://www.telerik.com/kendo-react-ui/components/dateinputs
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Calendar.Semantic.md) · [Interaction](./Calendar.Interaction.md) · [Accessibility](./Calendar.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #112 Calendar (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik Calendar baseline)

---

## 1. Root container

`inline-flex flex-col p-3 border border-border rounded-md bg-background w-[280px]`

---

## 2. Header row

`flex items-center justify-between mb-2`

Navigation buttons: `Button variant="ghost" size="icon"` with chevron icons.

Month/year label: `text-sm font-semibold text-foreground cursor-pointer hover:text-primary`

---

## 3. Weekday labels row

`grid grid-cols-7 mb-1`. Each cell: `text-center text-xs text-muted-foreground`

---

## 4. Day cells

`grid grid-cols-7 gap-y-1`. Each day button: `size-8 rounded-md text-sm`

States:
- Default: `hover:bg-accent hover:text-accent-foreground`
- Today: `bg-accent text-accent-foreground font-semibold`
- Selected: `bg-primary text-primary-foreground hover:bg-primary`
- In-range (range mode): `bg-accent/50 rounded-none`
- Range start/end: `bg-primary text-primary-foreground` + rounded only on outer edge
- Disabled: `text-muted-foreground opacity-50 cursor-not-allowed`
- Other-month day: `text-muted-foreground opacity-40`

---

## 5. Design tokens

Uses: `bg-background`, `border-border`, `text-foreground`, `text-muted-foreground`, `bg-accent`, `text-accent-foreground`, `bg-primary`, `text-primary-foreground`.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-3 (appearance axes — size/fillMode/rounded). Calendar adopts the subset it supports.
> Supersedes: nothing — additive.

### 6. Year / decade view grids

Year view: `grid grid-cols-3 gap-1`. Each month button: `py-2 rounded-md text-sm`. State matrix mirrors month day-cell states: selected = `bg-primary text-primary-foreground`; today's month = `bg-accent text-accent-foreground`; default = `hover:bg-accent hover:text-accent-foreground`.

Decade view: `grid grid-cols-2 gap-1` (5 rows × 2 cols for 10 years + overflow). Each year button: same recipe as month buttons.

### 7. FR-3 appearance axes (Calendar subset)

Calendar supports `size` only:

| size | Day cell | Header font | Width |
|---|---|---|---|
| `sm` | `size-7 text-xs` | `text-xs` | `w-[240px]` |
| `md` (default) | `size-8 text-sm` | `text-sm` | `w-[280px]` |
| `lg` | `size-10 text-sm` | `text-base` | `w-[320px]` |

`fillMode` and `rounded` do not apply to Calendar (border is structural, not stylistic on this component).

### 8. Focus ring (roving tabIndex cell)

The cell holding `tabIndex=0` receives `outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1` to distinguish keyboard focus from the hover state. This is distinct from the hover wash (`bg-accent/50`) — the ring must be visible simultaneously when the cell is both focused and hovered.
