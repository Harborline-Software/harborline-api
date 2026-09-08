# Form — Styling Contract

- **Component:** Form
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Form.Semantic.md) · [Interaction](./Form.Interaction.md) · [Accessibility](./Form.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Form.tsx`
- **Catalog row:** #63 Form (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Container

`flex flex-col` + gap class + `className` passthrough.

---

## 2. Gap classes

| `gap` | Classes |
|---|---|
| `sm` | `gap-3` |
| `md` (default) | `gap-5` |
| `lg` | `gap-8` |

---

## 3. Horizontal layout

When `layout='horizontal'`, a CSS variable `--form-label-width` is set as an inline style:

```css
--form-label-width: 180px; /* default, overridden by labelWidth prop */
```

Child `<FormField>` components with `layout='horizontal'` read this variable to align label widths.

---

## 4. `data-layout` attribute

`data-layout="stacked"` or `data-layout="horizontal"` — for CSS selector targeting by child components. Not a visual class.

---

## Wave UC-1 — FormController styling model

_Ruling: FR-1 (family-rulings-2026-06-11.md). Kendo minimum surface per 2026-06-12 re-audit._
_Status: Draft._

### UC-1.1 FormController has no visual footprint

`FormController` is a headless render-prop component. It renders no DOM of
its own — its `render`/`children` function is responsible for all visual
output. The M1 `Form` layout container (§1–§4 above) SHOULD be used inside
the `FormController` render prop for form layout and gap styling.

### UC-1.2 Disabled-state visual token

When the host derives `disabled` from `renderProps` state and threads it into
`FormField disabled`, the `FormField` chrome applies the standard disabled
palette per FormField.Styling (label: `text-gray-400`, hint: `text-gray-300`).
No additional `FormController`-level token is needed.

### UC-1.3 Submit button `canSubmit` visual state

The host SHOULD bind `!canSubmit` to the submit Button's `disabled` prop.
Button's disabled styling is governed by Button.Styling. `FormController`
owns no additional styling for the submit-gated state.

### UC-1.4 ValidationSummary placement token

When `FormController` errors are non-empty post-submit, the host places a
`ValidationSummary` at the top of the form. The gap between the
`ValidationSummary` and the first `FormField` SHOULD use the form's `gap`
token (M1 §2) — i.e. the summary is a peer child of the `Form` layout
container and inherits the flex column gap automatically.
