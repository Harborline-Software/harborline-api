# Icon — Accessibility Contract

- **Component:** Icon
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Icon.Semantic.md) · [Interaction](./Icon.Interaction.md) · [Accessibility](./Icon.Accessibility.md) · [Styling](./Icon.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Icon.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

The two exported components have opposite accessibility postures:

- **`Icon`**: always meaningful to AT — uses `role="img"` + `aria-label={name}`.
- **`SVGIcon`**: always decorative — SVG is `aria-hidden`; the wrapper `<span>` has no role.

Hosts must choose the right component for their context.

---

## 2. `Icon` ARIA

| Attribute | Value |
|---|---|
| `role` | `"img"` |
| `aria-label` | The `name` prop value (e.g., `"checkmark"`) |
| `data-icon` | Same `name` value (CSS selector hook) |

SR reads: `"{name}, image"`.

**Concern:** The `name` prop is a CSS identifier (e.g., `"k-i-check"` or `"arrow-up"`). These names may not be human-readable as accessible labels. A label like `"k-i-check"` is not useful to a screen reader user.

**Known gap (A1):** `aria-label` is set to the raw icon name which may be a CSS identifier, not a human-readable description. Hosts using `Icon` in a meaningful (non-decorative) context SHOULD ensure the `name` value is a legible English noun/verb, OR the component should add a separate `label` prop for the accessible name.

**WCAG citation:** WCAG 2.2 SC 1.1.1 Non-text Content; SC 4.1.2 Name, Role, Value.

---

## 3. `SVGIcon` ARIA

```html
<span class="inline-flex items-center justify-center ...">
  <svg aria-hidden="true" class="h-full w-full">...</svg>
</span>
```

| Attribute | Value | Rationale |
|---|---|---|
| `aria-hidden` on SVG | `"true"` | SVG is decorative |
| Role on wrapper span | none | Wrapper is a size/colour container only |
| `aria-label` | none | Not meaningful to AT |

SR ignores the icon entirely. This is correct when the icon is decorative (e.g., inside a button whose text label provides the accessible name).

**WCAG citation:** WCAG 2.2 SC 1.1.1 Non-text Content — decorative images should be hidden from AT.

---

## 4. Choosing `Icon` vs `SVGIcon`

| Usage | Correct component | Rationale |
|---|---|---|
| Icon-only button (no text) | `SVGIcon` + `aria-label` on the button | Button `aria-label` provides the accessible name; icon is decorative |
| Icon with adjacent text label | `SVGIcon` | Text label provides accessible name; icon decorates |
| Standalone status icon (no adjacent text) | `Icon` with a human-readable `name` | `role="img"` + `aria-label` is the accessible name |
| DataGrid status cell (icon only) | `Icon` with descriptive `name` | Same as above |

---

## 5. Keyboard

Neither component is focusable. No keyboard contract applies.

---

## 6. Known gaps

| # | Item | Severity | Resolution path |
|---|---|---|---|
| A1 | `Icon.aria-label` = raw CSS icon name, not human-readable | High (for meaningful icon usage) | Add a separate `label?: string` prop to `Icon` for the human-readable description |
| A2 | No validation that `SVGIcon` is always inside an accessible parent | Low | Dev-mode warning when rendered without an accessible parent context |
