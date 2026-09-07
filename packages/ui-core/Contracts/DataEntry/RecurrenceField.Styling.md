# RecurrenceField — Styling Contract

- **Component:** RecurrenceField
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RecurrenceField.Semantic.md) · [Interaction](./RecurrenceField.Interaction.md) · [Accessibility](./RecurrenceField.Accessibility.md) · [Styling](./RecurrenceField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/RecurrenceField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

RecurrenceField is a compound control with a vertical `space-y-3` layout.
Sub-controls use a shared `SELECT_CLS` recipe for selects/date inputs and a
distinct recipe for weekday toggle buttons. No size axis.

---

## 2. Token surface

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-recurrence-select-bg` | Select/date input background | `white` |
| `--sf-recurrence-select-border` | Select/date default border | `gray-300` |
| `--sf-recurrence-select-border-focus` | Focused border | `blue-500` |
| `--sf-recurrence-select-ring-focus` | Focused ring | `blue-500` |
| `--sf-recurrence-select-bg-disabled` | Disabled background | `gray-50` |
| `--sf-recurrence-select-fg` | Select text | `gray-900` |
| `--sf-recurrence-label-fg` | Label text | `gray-700` |
| `--sf-recurrence-inline-fg` | Inline text labels (`"Every"`, `"day(s)"`) | `gray-600` |
| `--sf-recurrence-weekday-bg` | Default weekday button background | `gray-100` |
| `--sf-recurrence-weekday-fg` | Default weekday text | `gray-700` |
| `--sf-recurrence-weekday-bg-hover` | Weekday hover | `gray-200` |
| `--sf-recurrence-weekday-selected-bg` | Selected weekday background | `blue-600` |
| `--sf-recurrence-weekday-selected-fg` | Selected weekday text | `white` |
| `--sf-recurrence-weekday-focus-ring` | Weekday focus ring | `blue-500` |
| `--sf-recurrence-interval-border` | Interval input border | `gray-300` |

---

## 3. Tailwind class recipes

### 3.1 Root container

```
space-y-3
```

### 3.2 Label

```
block text-sm font-medium text-gray-700
```

### 3.3 SELECT_CLS (shared by frequency select and end-date input)

```
rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900
focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500
disabled:bg-gray-50
```

Frequency select additionally: `w-full`.

### 3.4 Interval row

```
flex items-center gap-2
```

Inline labels:

```
text-sm text-gray-600
```

Interval input:

```
w-16 rounded-md border border-gray-300 px-2 py-2 text-sm text-center
focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500
disabled:bg-gray-50
```

### 3.5 Weekday group

```
flex flex-wrap gap-1.5
```

Weekday button — base:

```
rounded-full px-3 py-1 text-xs font-medium transition-colors
focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
```

Weekday button — selected:

```
bg-blue-600 text-white
```

Weekday button — unselected:

```
bg-gray-100 text-gray-700 hover:bg-gray-200
```

### 3.6 End date row

```
flex items-center gap-2
```

End date label:

```
text-sm text-gray-600 whitespace-nowrap
```

---

## 4. Visual state inventory

| Control | State | Recipe |
|---|---|---|
| Frequency select | Default | `border-gray-300` |
| Frequency select | Focused | `focus:border-blue-500 focus:ring-1 focus:ring-blue-500` |
| Interval input | Default | `border-gray-300` |
| Interval input | Focused | same as select |
| Weekday button | Default | `bg-gray-100 text-gray-700` |
| Weekday button | Selected | `bg-blue-600 text-white` |
| Weekday button | Hover (unselected) | `hover:bg-gray-200` |
| All controls | Disabled | `disabled:bg-gray-50` (inputs/selects); buttons inherit `disabled` opacity |

---

## 5. Focus ring inconsistency

Select, interval, and end-date inputs use `ring-1` (1px). Weekday buttons use
`ring-2` (2px). A future amendment should standardize to `ring-2` across all
sub-controls for WCAG 2.4.13 compliance.

---

## 6. Open questions

1. **`ring-1` vs `ring-2`.** Standardize to `ring-2` for all sub-controls.
2. **Interval input width.** `w-16` (64px) may be too narrow for `max={99}` on
   high-density displays. Consider `w-20`.
3. **Error state.** No error state is currently defined for any sub-control.
   Adding per-field error borders would require amending the shared recipe.
