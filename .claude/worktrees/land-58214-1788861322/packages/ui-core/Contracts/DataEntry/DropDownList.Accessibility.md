# DropDownList — Accessibility Contract

- **Component:** DropDownList
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DropDownList.Semantic.md) · [Interaction](./DropDownList.Interaction.md) · [Styling](./DropDownList.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DropDownList.tsx`
- **Catalog row:** #48 DropDownList (`app-priority: critical`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

The invisible native `<select>` provides full ARIA semantics. No additional ARIA attributes are needed on the custom overlay.

| Attribute | Element | Value |
|---|---|---|
| `id` | `<select>` | For `<label htmlFor>` association |
| `name` | `<select>` | Form participation |
| `disabled` | `<select>` | Disabled state |
| `value` | `<select>` | Current selection |

The `<span>` overlay elements are `pointer-events-none` and not in the AT tree.

---

## 2. AT behavior

Screen readers interact with the native `<select>` directly. Option text, disabled state, and selection are all exposed via native semantics. Keyboard navigation (arrow keys, type-ahead) works natively.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DDL3 | Medium | `aria-invalid` not set — error state not communicated to AT | Accepted-risk M1 |
| G-DDL4 | Low | `aria-label`/`aria-labelledby` not passed through — callers must use external `<label htmlFor>` | Accepted-risk M1 |
