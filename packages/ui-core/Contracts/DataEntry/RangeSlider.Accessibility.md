# RangeSlider — Accessibility Contract

- **Component:** RangeSlider
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RangeSlider.Semantic.md) · [Interaction](./RangeSlider.Interaction.md) · [Styling](./RangeSlider.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/RangeSlider.tsx`
- **Catalog row:** #109 RangeSlider (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `type="range"` | Low handle `<input>` | Implicit `role="slider"` |
| `aria-label="{ariaLabel} minimum"` | Low handle | e.g. `"Price minimum"` |
| `aria-valuemin={min}` | Low handle | Bound minimum |
| `aria-valuemax={high}` | Low handle | Dynamic — caps at current `high` |
| `aria-valuenow={low}` | Low handle | Current low value |
| `disabled` | Low handle | When disabled |
| `type="range"` | High handle `<input>` | Implicit `role="slider"` |
| `aria-label="{ariaLabel} maximum"` | High handle | e.g. `"Price maximum"` |
| `aria-valuemin={low}` | High handle | Dynamic — floor at current `low` |
| `aria-valuemax={max}` | High handle | Bound maximum |
| `aria-valuenow={high}` | High handle | Current high value |
| `disabled` | High handle | When disabled |

---

## 2. Dynamic bounds

`aria-valuemax` on the low handle updates to reflect the current `high` (upper bound narrows as high handle moves). `aria-valuemin` on the high handle updates to reflect the current `low`. This tells AT users the constrained valid range for each handle.

---

## 3. Group labeling

The outer `<div>` has no group role. For AT users to understand both sliders form one control, the host should label the outer container:
```tsx
<fieldset>
  <legend>Price range</legend>
  <RangeSlider ariaLabel="Price" ... />
</fieldset>
```

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-RS2 | Low | No outer `role="group"` — two sibling sliders look unrelated to AT without host fieldset | Accepted-risk M1; host provides fieldset wrapper |
