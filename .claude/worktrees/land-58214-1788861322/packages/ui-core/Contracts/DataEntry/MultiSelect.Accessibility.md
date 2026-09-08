# MultiSelect — Accessibility Contract

- **Component:** MultiSelect
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./MultiSelect.Semantic.md) · [Interaction](./MultiSelect.Interaction.md) · [Styling](./MultiSelect.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MultiSelect.tsx`
- **Catalog row:** #86 MultiSelect (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `role="combobox"` | Trigger `<div>` | Combobox role |
| `aria-expanded={open}` | Trigger `<div>` | Popover open state |
| `aria-controls={listId}` | Trigger `<div>` | References the listbox |
| `aria-invalid={error ? true : undefined}` | Trigger `<div>` | Error state |
| `aria-describedby={describedBy}` | Trigger `<div>` | From FormField context |
| `id={id}` | Trigger `<div>` | From FormField context |
| `role="listbox"` | PopoverContent | Option container |
| `aria-multiselectable="true"` | PopoverContent | Multiple selection |
| `role="option"` | Each option `<div>` | Option role |
| `aria-selected={selected}` | Each option `<div>` | Selected state |
| `aria-disabled={opt.disabled}` | Each option `<div>` | Disabled state |
| `aria-autocomplete="list"` | Search `<input>` | Inline |
| `aria-label="Remove {label}"` | Chip `×` `<button>` | Accessible remove label |

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-MS1 | High | No `aria-activedescendant` on trigger — keyboard-active option not announced | Blocking-before-v1-ship — WCAG SC 4.1.2 (Level A) violation; must resolve before v1 ship |
| G-MS2 | High | No ArrowDown/Up keyboard navigation — keyboard-only users cannot select options | Blocking-before-v1-ship — WCAG SC 2.1.1 (Level A) violation; must resolve before v1 ship |
| G-MS4 | Low | `onOpenAutoFocus` prevented — AT focus stays on trigger when listbox opens | Accepted-risk M1 |
| G-MS5 | Low | Chip overflow "+N more" text has no ARIA — excess selections are not announced | Accepted-risk M1 |

> **⚠ Blocking note:** `role="combobox"` without keyboard navigation (G-MS2) is a WCAG 2.1.1 hard failure. Before shipping MultiSelect for production use, either: (a) remove `role="combobox"` from the trigger and use a simpler role, OR (b) fix G-MS2 (add ArrowDown/Up keyboard navigation). The current implementation ships the ARIA role without the required keyboard support.
