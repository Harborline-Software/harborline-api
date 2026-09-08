# Tag — Accessibility Contract

- **Component:** Tag
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Tag.Semantic.md) · [Interaction](./Tag.Interaction.md) · [Accessibility](./Tag.Accessibility.md) · [Styling](./Tag.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Tag.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

Tag renders a `<span>` with optional remove `<button>`. Its accessibility contract covers: root element semantics, remove button naming, icon decorativity, disabled state, and keyboard behaviour.

---

## 2. Root element

Tag renders as `<span>` (not `<button>` or `<div>`). It is an inline element.

| Attribute | Value | Notes |
|---|---|---|
| Element | `<span>` | Inline; no implicit ARIA role |
| Default ARIA role | none | Non-interactive; AT reads as inline text |
| `aria-label` | Not set | `label` text content IS the accessible name |
| `tabIndex` | Not set | Tag body is not focusable |

When `onRemove` is absent, Tag is purely presentational — SR reads the label as inline text.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 3. Leading icon

```html
<span class="shrink-0 ..." aria-hidden="true">{icon}</span>
```

The icon wrapper is `aria-hidden`. The `label` prop provides the accessible name. This correctly satisfies WCAG 2.2 SC 1.1.1 Non-text Content for decorative icons.

---

## 4. Remove button

```html
<button
  type="button"
  disabled={disabled}
  aria-label={`Remove ${label}`}
>
  <svg aria-hidden="true">…</svg>
</button>
```

| Attribute | Value | Notes |
|---|---|---|
| `aria-label` | `"Remove {label}"` | Specific and descriptive: `"Remove Kitchen Renovation"` |
| `disabled` | `{disabled}` HTML attr | Prevents activation when tag is disabled |
| SVG `aria-hidden` | `"true"` | Icon is decorative |

SR reads: `"Remove Kitchen Renovation, button"` (enabled) or `"Remove Kitchen Renovation, button, dimmed"` (disabled).

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 5. `disabled` state

When `disabled={true}`:
- Remove button is `disabled` (native HTML attribute).
- SR announces the button as "dimmed" or "unavailable" (varies by browser/SR).
- Tag opacity is `opacity-60` — this is a visual-only cue. SR does not announce the tag label differently.

**Note:** The tag label text is not announced as disabled — only the remove button is disabled. This is correct; the tag's existence/label is still meaningful to AT.

---

## 6. Color contrast

Tag uses a tinted palette: `bg-{color}-50`, `text-{color}-700`, `ring-{color}-200` (ring-inset border). All standard colour combinations should meet WCAG 2.2 SC 1.4.3.

| Colour | Text | Background | Approx ratio |
|---|---|---|---|
| `gray` | `text-gray-700` | `bg-gray-100` | ~9.7:1 — passes |
| `blue` | `text-blue-700` | `bg-blue-50` | ~6.1:1 — passes |
| `green` | `text-green-700` | `bg-green-50` | ~6.4:1 — passes |
| `amber` | `text-amber-700` | `bg-amber-50` | ~5.9:1 — passes |
| `red` | `text-red-700` | `bg-red-50` | ~5.8:1 — passes |
| `purple` | `text-purple-700` | `bg-purple-50` | ~6.2:1 — passes |
| `teal` | `text-teal-700` | `bg-teal-50` | ~5.5:1 — passes |

All combinations pass WCAG 2.2 SC 1.4.3 (4.5:1 minimum).

Remove button icon colour (slightly lighter, e.g., `text-blue-500`) against tag background may approach 3:1 (non-text contrast). Verify per colour.

---

## 7. Keyboard

| Key | Element | Action |
|---|---|---|
| Tab | Remove button | Focuses (when present and not disabled) |
| Enter / Space | Remove button | Fires `onRemove()` |
| Shift+Tab | Remove button | Moves focus back to previous element |

Tag body: not in tab order.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard.

---

## 8. Touch target

The remove button has `p-0.5` padding on an SVG icon. Approximate target size: ~18–20px. Below the WCAG 2.2 SC 2.5.8 minimum of 24×24px.

**Known gap (A1):** Remove button touch target too small. PAO Styling should increase to `p-1.5` minimum.

---

## 9. Known gaps

| # | Item | Severity | Resolution path |
|---|---|---|---|
| A1 | Remove button touch target below 24×24px | Medium | PAO Styling increases padding |
| A2 | `opacity-60` on disabled tag communicates disabled via colour/opacity alone | Low | Add `aria-disabled="true"` to root span when `disabled=true` for AT signal |
