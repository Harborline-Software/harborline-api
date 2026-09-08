# ScheduleField — Styling Contract

- **Component:** ScheduleField
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ScheduleField.Semantic.md) · [Interaction](./ScheduleField.Interaction.md) · [Accessibility](./ScheduleField.Accessibility.md) · [Styling](./ScheduleField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ScheduleField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

ScheduleField renders as a `<fieldset>` with a vertical `space-y-3` layout.
The date and time inputs use a shared `INPUT_CLS` recipe. The all-day checkbox
uses a Tailwind-styled checkbox. Sub-control labels are `text-xs` — subdued
compared to the `text-sm` FormField label standard.

---

## 2. Token surface

| Token | Semantic role | Notes |
|---|---|---|
| `--sf-schedule-input-bg` | Input background | `white` |
| `--sf-schedule-input-border` | Default border | `gray-300` |
| `--sf-schedule-input-border-focus` | Focused border | `blue-500` |
| `--sf-schedule-input-ring-focus` | Focused ring | `blue-500` |
| `--sf-schedule-input-bg-disabled` | Disabled background | `gray-50` |
| `--sf-schedule-input-fg-disabled` | Disabled text | `gray-400` |
| `--sf-schedule-legend-fg` | Legend text | `gray-700` |
| `--sf-schedule-sublabel-fg` | Sub-control label text | `gray-500` |
| `--sf-schedule-allday-fg` | All-day label text | `gray-600` |
| `--sf-schedule-allday-ring-focus` | Checkbox focus ring | `blue-500` |

---

## 3. Tailwind class recipes

### 3.1 Root fieldset

```
space-y-3
```

### 3.2 Legend

```
text-sm font-medium text-gray-700
```

### 3.3 INPUT_CLS (shared by date, start time, end time)

```
rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-900
focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500
disabled:bg-gray-50 disabled:text-gray-400
```

### 3.4 Sub-control row (date + time inputs)

```
flex items-center gap-2 flex-wrap
```

Each sub-control sits in a `<div>` column:

```
(block layout — no additional class beyond the div wrapper)
```

Sub-control label:

```
block text-xs text-gray-500 mb-1
```

### 3.5 All-day checkbox

```
flex items-center gap-2 cursor-pointer
```

Checkbox element:

```
h-4 w-4 rounded border-gray-300 text-blue-600 focus:ring-blue-500
```

All-day span:

```
text-sm text-gray-600
```

---

## 4. Visual state inventory

| Control | State | Recipe |
|---|---|---|
| Date / time inputs | Default | `border-gray-300` |
| Date / time inputs | Focused | `focus:border-blue-500 focus:ring-1 focus:ring-blue-500` |
| Date / time inputs | Disabled | `disabled:bg-gray-50 disabled:text-gray-400` |
| Checkbox | Default | `border-gray-300 text-blue-600` |
| Checkbox | Focused | `focus:ring-blue-500` |

No error state defined.

---

## 5. Sub-control label size

Sub-control labels use `text-xs text-gray-500` — notably smaller and more
subdued than the `text-sm font-medium text-gray-700` of standard FormField
labels. This creates a visual hierarchy where the `<legend>` is the primary
label and sub-control labels are secondary.

PAO should confirm this hierarchy is intentional or align sub-labels with the
standard FormField label treatment.

---

## 6. Focus ring

`ring-1` (1px) is used throughout. Upgrade to `ring-2` for WCAG 2.4.13
compliance (see Accessibility §8 Gap A-3).

---

## 7. Open questions

1. **`ring-1` upgrade.** Standardize to `ring-2` for WCAG 2.4.13.
2. **Error state.** Define error borders for date/time inputs.
3. **Sub-label font size.** Confirm `text-xs` is intentional vs `text-sm`
   standard.
4. **Responsive layout.** `flex-wrap` on the date/time row handles narrow
   viewports. Confirm this is the desired break behaviour.
