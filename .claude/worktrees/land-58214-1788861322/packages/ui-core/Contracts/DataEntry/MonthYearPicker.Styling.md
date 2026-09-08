# MonthYearPicker — Styling Contract

- **Component:** MonthYearPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MonthYearPicker.Semantic.md) · [Interaction](./MonthYearPicker.Interaction.md) · [Accessibility](./MonthYearPicker.Accessibility.md) · [Styling](./MonthYearPicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MonthYearPicker.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

MonthYearPicker renders a bordered panel containing a year navigation row and
a 4×3 month grid. The visual surface spans: **outer container**, **label**,
**year navigation bar**, **year display**, **month buttons**, and **error
message**. No size axis.

---

## 2. Token surface

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-monthpicker-bg` | Container background | `white` |
| `--sf-monthpicker-border` | Default container border | `gray-300` |
| `--sf-monthpicker-border-error` | Error container border | `red-500` |
| `--sf-monthpicker-disabled-opacity` | Disabled opacity | `0.5` |
| `--sf-monthpicker-year-fg` | Year text colour | `gray-900` |
| `--sf-monthpicker-nav-hover-bg` | Nav button hover fill | `gray-100` |
| `--sf-monthpicker-nav-disabled-opacity` | Disabled nav button opacity | `0.3` |
| `--sf-monthpicker-month-fg` | Default month text | `gray-700` |
| `--sf-monthpicker-month-hover-bg` | Default month hover fill | `gray-100` |
| `--sf-monthpicker-month-selected-bg` | Selected month fill | `blue-600` |
| `--sf-monthpicker-month-selected-fg` | Selected month text | `white` |
| `--sf-monthpicker-month-current-bg` | Current month fill | `blue-50` |
| `--sf-monthpicker-month-current-fg` | Current month text | `blue-700` |
| `--sf-monthpicker-month-current-ring` | Current month ring | `blue-300` |
| `--sf-monthpicker-error-fg` | Error message text | `red-600` |
| `--sf-monthpicker-label-fg` | Label text | `gray-700` |
| `--sf-monthpicker-required-fg` | Required asterisk colour | `red-500` |

---

## 3. Tailwind class recipes

### 3.1 Outer flex container

```
flex flex-col gap-1
```

### 3.2 Label

```
text-sm font-medium text-gray-700
```

Required asterisk:

```
text-red-500 ml-1
```

### 3.3 Picker container panel

```
border rounded-md bg-white overflow-hidden
```

Default border:

```
border-gray-300
```

Error border override:

```
border-red-500
```

Disabled overlay:

```
opacity-50
```

### 3.4 Year navigation row

```
flex items-center justify-between px-3 py-2 border-b border-gray-100
```

Year nav button:

```
p-1 rounded hover:bg-gray-100 disabled:opacity-30 disabled:cursor-not-allowed transition-colors
```

Year display:

```
text-sm font-semibold text-gray-900 w-12 text-center
```

### 3.5 Month grid

```
grid grid-cols-4 gap-1 p-2
```

### 3.6 Month button — state matrix

| State | Tailwind |
|---|---|
| Base | `py-1.5 text-sm rounded-md transition-colors font-medium` |
| Selected | `bg-blue-600 text-white` |
| Current month (not selected) | `bg-blue-50 text-blue-700 ring-1 ring-blue-300` |
| Default | `text-gray-700 hover:bg-gray-100` |
| Disabled | `cursor-not-allowed` (additional) |

State precedence: selected > current > default.

### 3.7 Error message

```
text-xs text-red-600
```

---

## 4. Visual state inventory

| State | Condition | Affected region |
|---|---|---|
| Default | no selection, not current | Month button: `text-gray-700 hover:bg-gray-100` |
| Selected | `parsed.month === month && parsed.year === displayYear` | Month button: `bg-blue-600 text-white` |
| Current month | `now.getMonth() === idx && now.getFullYear() === displayYear` | Month button: `bg-blue-50 text-blue-700 ring-1 ring-blue-300` |
| Error | `error` truthy | Container: `border-red-500`; message: `text-red-600` |
| Disabled | `disabled === true` | Container: `opacity-50`; all buttons: `cursor-not-allowed` |

---

## 5. Open questions

1. **Focus ring on month buttons.** No explicit `focus-visible:ring-*` recipe
   is present. Align with the fleet focus ring standard (at minimum
   `focus-visible:ring-2 focus-visible:ring-blue-500`).
2. **Size axis.** A compact size variant would be useful in sidebar layouts.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-3 (size axis — resolves open question 2), FR-1 (error state — resolves token hardcoding).
> Supersedes: open question 1 (focus ring) — RESOLVED; open question 2 (size axis) — RESOLVED.

### 6. Focus ring on month buttons (resolves open question 1)

Add to month button base recipe:

```
focus:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-1
```

`ring-ring` is the design-system token. Replace the hardcoded `ring-blue-500` referenced in A-4. The roving-tabIndex cell (the one with `tabIndex=0`) uses this ring; non-focused cells (`tabIndex=-1`) do not show a ring until keyboard-navigated.

### 7. Size axis (resolves open question 2 — FR-3)

| size | Month button | Nav bar padding | Container panel |
|---|---|---|---|
| `sm` | `py-1 text-xs` | `px-2 py-1` | compact |
| `md` (default) | `py-1.5 text-sm` | `px-3 py-2` | current default |
| `lg` | `py-2 text-base` | `px-4 py-3` | expanded |

### 8. Token migration

Replace remaining hardcoded values with design tokens:

| M1 hardcoded | Wave-N token |
|---|---|
| `border-gray-300` | `border-input` |
| `bg-white` | `bg-background` |
| `border-red-500` | `border-destructive` |
| `text-red-600` | `text-destructive` |
| `text-red-500` (required asterisk) | `text-destructive` |
| `text-gray-700` (month text) | `text-foreground` |
| `hover:bg-gray-100` | `hover:bg-muted` |
| `bg-blue-600` (selected) | `bg-primary` |
| `text-white` (selected) | `text-primary-foreground` |
| `bg-blue-50` (current month) | `bg-primary/10` |
| `text-blue-700` (current month) | `text-primary` |
| `ring-blue-300` (current month ring) | `ring-primary/40` |
| `text-gray-900` (year display) | `text-foreground` |
| `text-gray-700` (label) | `text-foreground` |
| `hover:bg-gray-100` (nav button) | `hover:bg-muted` |

### 9. Disabled month styling (Wave-N)

Disabled month buttons (from Wave-N `disabledMonths`/`minMonth`/`maxMonth`):

```
opacity-50 cursor-not-allowed text-muted-foreground
```

State precedence: selected > current > disabled > default.
