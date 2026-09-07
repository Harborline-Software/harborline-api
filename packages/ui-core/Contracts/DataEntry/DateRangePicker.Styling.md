# DateRangePicker — Styling Contract

- **Component:** DateRangePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DateRangePicker.Semantic.md) · [Interaction](./DateRangePicker.Interaction.md) · [Accessibility](./DateRangePicker.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateRangePicker.tsx`
- **Catalog row:** #39 DateRangePicker (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Wrapper

`relative inline-block` + `className` passthrough.

---

## 2. Trigger button

Base: `flex items-center gap-2 rounded-xl border border-gray-300 bg-white px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500`

Disabled: `cursor-not-allowed opacity-50`

Normal hover: `cursor-pointer hover:border-gray-400`

Text color: `text-gray-400` when placeholder, `text-gray-900` when value set.

> **M1 note:** Hardcoded `border-gray-300`, `bg-white`, `ring-blue-500` — not design tokens.

---

## 3. Popover

`absolute top-full left-0 mt-1 z-50 flex gap-6 rounded-xl border border-gray-200 bg-white p-4 shadow-lg`

---

## 4. Month header

Month name: `text-center text-sm font-semibold text-gray-800 mb-2`

Nav buttons: `h-7 w-7 rounded-full flex items-center justify-center text-gray-400 hover:bg-gray-100`

---

## 5. Day grid

`grid grid-cols-7 gap-0`

Weekday headers: `text-center text-xs text-gray-400 py-1`

---

## 6. Day button states

Base: `h-8 w-8 text-xs font-medium rounded-full transition-colors focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500`

| State | Classes |
|---|---|
| Start / End | `bg-blue-600 text-white hover:bg-blue-700` |
| In range | `bg-blue-100 text-blue-800 rounded-none` |
| Today (unselected) | `border border-blue-400 text-blue-700 hover:bg-blue-50` |
| Normal | `text-gray-700 hover:bg-gray-100` |
| Disabled | `text-gray-200 cursor-not-allowed` |

> **M1 note:** All colors hardcoded (`blue-600`, `blue-100`, `gray-100`, etc.).

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-3 (size/fillMode/rounded — resolves G-DRP4 and audit P1 "size declared but not applied").
> Supersedes: §2 trigger button — replaces hardcoded classes with tokenized size/fillMode/rounded variants.

### 7. Trigger button — FR-3 size/fillMode/rounded (resolves G-DRP4)

The trigger button now uses the same size/fillMode/rounded token classes as DateInput (Wave-N Styling §6–§7):

**fillMode recipes (trigger button):**

| fillMode | Classes |
|---|---|
| `solid` | `bg-background border border-input` |
| `outline` | `bg-transparent border border-input` |
| `flat` | `bg-transparent border-b border-input` |

**rounded recipes:**

| rounded | Classes |
|---|---|
| `none` | `rounded-none` |
| `sm` | `rounded` |
| `md` | `rounded-md` |
| `lg` | `rounded-lg` |
| `full` | `rounded-full` |

**size recipes:**

| size | Classes |
|---|---|
| `sm` | `h-7 text-sm px-2` |
| `md` | `h-9 text-sm px-3` |
| `lg` | `h-11 text-base px-4` |

Legacy aliases (`'small'|'medium'|'large'`) retained through Wave-N+2.

### 8. Error state styling (FR-1)

When `error=true`: trigger border becomes `border-destructive focus:ring-destructive`. This supersedes the M1 hardcoded `border-gray-300`.

### 9. Token migration (Wave-N)

Replace all remaining M1 hardcoded colors:

| M1 hardcoded | Wave-N token |
|---|---|
| `border-gray-300` | `border-input` |
| `bg-white` | `bg-background` |
| `ring-blue-500` | `ring-ring` |
| `border-gray-200` (popover) | `border-border` |
| `bg-blue-600` (day selected) | `bg-primary` |
| `text-white` (day selected) | `text-primary-foreground` |
| `bg-blue-100` (in-range) | `bg-primary/20` |
| `text-blue-800` (in-range) | `text-primary` |
| `border-blue-400` (today) | `border-primary` |
| `hover:bg-gray-100` | `hover:bg-muted` |
