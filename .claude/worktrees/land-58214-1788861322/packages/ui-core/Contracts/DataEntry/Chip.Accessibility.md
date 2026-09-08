# Chip — Accessibility Contract

- **Component:** Chip
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Chip.Semantic.md) · [Interaction](./Chip.Interaction.md) · [Styling](./Chip.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/Chip.tsx`
- **Catalog rows:** #25 Chip · #26 ChipList
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `<button type="button">` | Chip root | Native button — keyboard focusable |
| `aria-pressed={isSelected}` | Chip root | `true` when selected; `false` when not |
| `disabled` | Chip root | Boolean attribute when `disabled=true` |
| `role="button"` | Remove `<span>` | Marks remove control as interactive (M1 gap) |
| `aria-label="Remove"` | Remove `<span>` | Names icon-only control |
| `tabIndex={-1}` | Remove `<span>` | Removes from tab order (accessed via mouse only in M1) |

---

## 2. Selection announcement

`aria-pressed` communicates the toggle state:
- `aria-pressed="false"` → AT reads `"[chip text], button, not pressed"`
- `aria-pressed="true"` → AT reads `"[chip text], button, pressed"`

---

## 3. ChipList group

ChipList renders a `<div className="flex flex-wrap gap-1.5">` with no group role. For AT users to understand the collection, the host should wrap ChipList in a labeled container:
```html
<fieldset aria-label="Filter by status">
  <ChipList ... />
</fieldset>
```

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CH2 | Medium | Remove `<span>` has `role="button"` not `<button>` — keyboard access limited | Accepted-risk M1; remove is mouse/touch only |
| G-CH3 | Low | ChipList has no group role for AT users to understand the collection boundary | Accepted-risk M1; host provides fieldset wrapper |
