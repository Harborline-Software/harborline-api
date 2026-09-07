# Icon — Accessibility Contract

- **Component:** Icon
- **ADR 0017 family:** Typography
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Icon.Semantic.md) · [Interaction](./Icon.Interaction.md) · [Styling](./Icon.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Icon.tsx`
- **Catalog row:** #70 Icon (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

### Icon

| Attribute | Element | Value |
|---|---|---|
| `role="img"` | Root `<span>` | Marks the glyph as an image for AT |
| `aria-label={name}` | Root `<span>` | Uses `name` prop as the accessible label |

`Icon` uses `role="img"` + `aria-label` — the icon is announced by name. The `name` prop should be a meaningful description (e.g., `"calendar"`, `"delete"`, `"warning"`).

### SVGIcon

| Attribute | Element | Value |
|---|---|---|
| `aria-hidden="true"` | Inner `<svg>` | SVG is decorative; the wrapper `<span>` provides no label |

`SVGIcon` treats the icon as decorative. If the icon conveys meaning without adjacent text, the host must add `aria-label` to a wrapping button or provide adjacent visually-hidden text.

---

## 2. Decorative vs semantic icons

| Use case | Approach |
|---|---|
| Icon adjacent to visible text label | `SVGIcon` (or `Icon` — `aria-label` is redundant with label) |
| Icon-only button (no visible label) | Wrap in `<button aria-label="Action name"><SVGIcon /></button>` |
| Status icon that conveys meaning | Use `Icon` with a meaningful `name`; or add a visually-hidden `<span>` |

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-IC1 | Medium | `Icon` always announces its name even when decorative (adjacent to a label) | Accepted-risk M1; host passes `aria-hidden` via `className` workaround or wraps in `aria-hidden` span |
