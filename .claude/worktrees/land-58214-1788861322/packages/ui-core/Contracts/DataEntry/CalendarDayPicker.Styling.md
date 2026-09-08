# CalendarDayPicker — Styling Contract

- **Component:** CalendarDayPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CalendarDayPicker.Semantic.md) · [Interaction](./CalendarDayPicker.Interaction.md) · [Accessibility](./CalendarDayPicker.Accessibility.md) · [Styling](./CalendarDayPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CalendarDayPicker.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

CalendarDayPicker is an inline calendar widget. Its visual surface spans six
zones: **root container**, **nav header**, **weekday header row**, **day cell
buttons**, **today highlight**, and **mark decorations** (dot / highlight
variant). There is no size axis — the calendar renders at a fixed scale.

---

## 2. Token surface

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-calendar-bg` | Root background | `white` default |
| `--sf-calendar-header-fg` | Month/year label colour | `gray-900` |
| `--sf-calendar-weekday-fg` | Weekday header label colour | `gray-400` |
| `--sf-calendar-day-fg` | Enabled day number colour | `gray-700` |
| `--sf-calendar-day-fg-disabled` | Out-of-range day number colour | `gray-200` |
| `--sf-calendar-day-bg-hover` | Day button hover fill | `gray-100` |
| `--sf-calendar-day-bg-selected` | Selected day fill | `blue-600` |
| `--sf-calendar-day-fg-selected` | Selected day text | `white` |
| `--sf-calendar-day-bg-hover-selected` | Selected day hover fill | `blue-700` |
| `--sf-calendar-today-border` | Today ring border colour | `blue-400` |
| `--sf-calendar-today-fg` | Today number colour | `blue-700` |
| `--sf-calendar-today-bg-hover` | Today hover fill | `blue-50` |
| `--sf-calendar-mark-dot-default` | Default dot colour (when `mark.color` absent) | `#3b82f6` (blue-500) |
| `--sf-calendar-mark-highlight-bg` | Highlight variant background | `amber-100` |
| `--sf-calendar-nav-fg` | Navigation button icon colour | `gray-500` |
| `--sf-calendar-nav-bg-hover` | Navigation button hover fill | `gray-100` |
| `--sf-calendar-disabled-opacity` | Whole-component disabled opacity | `0.5` |
| `--sf-calendar-focus-ring` | Focus ring colour on buttons | `blue-500` |

---

## 3. Tailwind class recipes

### 3.1 Root container

```
inline-block select-none
```

When `disabled === true`, additionally:

```
opacity-50 pointer-events-none
```

### 3.2 Navigation header

```
flex items-center justify-between mb-3 px-1
```

Navigation buttons:

```
h-8 w-8 rounded-full flex items-center justify-center text-gray-500
hover:bg-gray-100 focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
disabled:opacity-30 disabled:cursor-not-allowed transition-colors
```

Month/year label:

```
text-sm font-semibold text-gray-900
```

### 3.3 Weekday header cells

```
text-center text-xs font-medium text-gray-400 py-1
```

### 3.4 Day grid

```
grid grid-cols-7 gap-0
```

Day cell wrapper:

```
flex justify-center py-0.5
```

### 3.5 Day button — state matrix

| State | Tailwind |
|---|---|
| Base (all days) | `relative h-8 w-8 rounded-full text-sm font-medium transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500` |
| Selected | `bg-blue-600 text-white hover:bg-blue-700` |
| Today (not selected) | `border-2 border-blue-400 text-blue-700 hover:bg-blue-50` |
| Disabled (out of range) | `text-gray-200 cursor-not-allowed` |
| Default (enabled, not today, not selected) | `text-gray-700 hover:bg-gray-100` |
| Highlight mark overlay | `bg-amber-100` (applied additionally) |

State precedence: `selected` > `today` > `disabled` > `default`.
Highlight mark applies as an additional overlay only on non-selected days.

### 3.6 Dot mark

```
absolute bottom-1 left-1/2 -translate-x-1/2 h-1 w-1 rounded-full
```

Background color set via inline `style={{ background: mark.color ?? '#3b82f6' }}`.

---

## 4. Visual state inventory

| State | Affected region | Recipe |
|---|---|---|
| Default day | day button | `text-gray-700 hover:bg-gray-100` |
| Today | day button | `border-2 border-blue-400 text-blue-700 hover:bg-blue-50` |
| Selected | day button | `bg-blue-600 text-white hover:bg-blue-700` |
| Disabled (range) | day button | `text-gray-200 cursor-not-allowed` |
| Focus-visible | any button | `focus-visible:ring-2 focus-visible:ring-blue-500` |
| Globally disabled | root wrapper | `opacity-50 pointer-events-none` |
| Dot mark | `<span>` inside button | small coloured circle, `h-1 w-1 rounded-full` |
| Highlight mark | day button background | `bg-amber-100` overlay |

---

## 5. Responsive behaviour

CalendarDayPicker uses `inline-block` — it shrinks to its intrinsic content
width. The 7-column grid is fixed at 7 × `h-8 w-8` = 7 × 32px ≈ 224px minimum.
No responsive breakpoints are defined. Hosts should ensure the container is
at least 240px wide.

---

## 6. Open questions

1. **Size variant.** A `size` prop (`'sm' | 'md' | 'lg'`) could scale the
   day cell from `h-8 w-8` to `h-10 w-10` for touch-first layouts.
2. **Dark mode tokens.** No dark-mode variants are currently defined. A future
   amendment should define `--sf-calendar-*-dark` overrides.
3. **Mark dot color enforcement.** The `mark.color` inline style bypasses the
   token surface. A recommended set of named mark color tokens would prevent
   contrast failures.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-3 (size axis — resolves open question 1), FR-1 (error border token).
> Supersedes: open question 1 (size variant) — RESOLVED.

### 7. Size axis (FR-3 — resolves open question 1)

| size | Day cell | Header font | Min-width |
|---|---|---|---|
| `sm` | `h-7 w-7 text-xs` | `text-xs` | `~200px` |
| `md` (default) | `h-8 w-8 text-sm` | `text-sm` | `~240px` |
| `lg` | `h-10 w-10 text-sm` | `text-base` | `~290px` |

### 8. Error state styling (FR-1)

When `error=true`: add `ring-2 ring-destructive` to the root container (light ring, not a border, so it does not displace inline layout). This gives a visible error indicator without changing the calendar's layout width.

### 9. Token migration

Replace remaining hardcoded color values with design-system tokens:

| M1 hardcoded | Wave-N token |
|---|---|
| `bg-blue-600` (selected day) | `bg-primary` |
| `text-white` (selected day) | `text-primary-foreground` |
| `hover:bg-blue-700` (selected hover) | `hover:bg-primary/90` |
| `border-blue-400` (today ring) | `border-primary` |
| `text-blue-700` (today text) | `text-primary` |
| `hover:bg-blue-50` (today hover) | `hover:bg-primary/10` |
| `text-gray-700` (enabled day) | `text-foreground` |
| `hover:bg-gray-100` (day hover) | `hover:bg-muted` |
| `text-gray-200` (disabled day) | `text-muted-foreground/40` |
| `text-gray-500` (nav icon) | `text-muted-foreground` |
| `hover:bg-gray-100` (nav button hover) | `hover:bg-muted` |
| `text-gray-400` (weekday header) | `text-muted-foreground` |
| `text-gray-900` (month/year label) | `text-foreground` |
| `ring-blue-500` (focus ring) | `ring-ring` |

### 10. Roving tabIndex focus ring

The day cell holding `tabIndex=0` uses `focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1`. This is distinct from the hover wash — the ring must be visible simultaneously when focused and hovered. All other cells carry `tabIndex=-1` and do not show a ring until navigated to.
