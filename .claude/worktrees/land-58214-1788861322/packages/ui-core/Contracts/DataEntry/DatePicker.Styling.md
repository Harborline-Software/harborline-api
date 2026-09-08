# DatePicker — Styling Contract

- **Component:** DatePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DatePicker.Semantic.md) · [Interaction](./DatePicker.Interaction.md) · [Accessibility](./DatePicker.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DatePicker.tsx`
- **Catalog row:** #38 DatePicker (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`relative` + `className` passthrough.

---

## 2. Input row

`flex items-center`

DateInput: `flex-1` (inherits DateInput's own styling)

Calendar button: `ml-1 p-1.5 rounded-md hover:bg-muted text-muted-foreground` — content: 📅 emoji.

---

## 3. Calendar popover

`absolute z-50 mt-1 bg-popover border border-border rounded-md shadow-lg`

---

## 4. Calendar grid

`p-3 select-none` (Calendar wrapper)

Header: `flex items-center justify-between mb-2`

Prev/next buttons: `px-1 hover:bg-muted rounded`

Month label: `text-sm font-medium`

Grid: `grid gap-0.5 grid-cols-7` (or `grid-cols-8` when `weekNumber=true`)

Day header cells: `text-xs text-muted-foreground text-center py-1`

---

## 5. Day button states

Base: `text-xs w-7 h-7 rounded-md flex items-center justify-center`

| State | Classes |
|---|---|
| Selected | `bg-primary text-primary-foreground` |
| Today (unselected) | `border border-primary` |
| Normal | `hover:bg-muted` |
| Disabled | `opacity-30 cursor-not-allowed` |

> **M1 note:** Selected and today states use `bg-primary` / `border-primary` design tokens.

---

## 6. Footer

`border-t p-2` · "Today" link: `text-xs text-primary hover:underline`

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-3 (appearance axes via DateInput inheritance).
> Supersedes: §2 calendar button — emoji replaced by SVG (G-DP8 resolved in Accessibility Wave-N §3).

### 7. Calendar toggle button (Wave-N)

Replace the 📅 emoji with a SVG calendar icon (`aria-hidden="true"`). Button classes: `ml-1 p-1.5 rounded-md hover:bg-muted text-muted-foreground focus-visible:ring-2 focus-visible:ring-ring` (adds explicit focus ring to existing recipe).

### 8. Popup dialog wrapper (Wave-N)

Popup gains `role="dialog"` (see Accessibility Wave-N §4). Styling unchanged: `absolute z-50 mt-1 bg-popover border border-border rounded-md shadow-lg`. Add `outline-none` to prevent native focus outline on the dialog container.

### 9. Year / decade view styling

Inherits Calendar Wave-N Styling §6. The DatePicker's internal Calendar renders the same year/decade grid recipes within the existing popup shell.

### 10. FR-3 size axis inheritance

The DatePicker outer trigger input inherits the DateInput Wave-N size vocabulary migration (FR-3: `'sm'|'md'|'lg'`). The calendar popover dimensions are fixed regardless of `size`.
