# Switch — Accessibility Contract

- **Component:** Switch
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Switch.Semantic.md) · [Interaction](./Switch.Interaction.md) · [Styling](./Switch.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Switch.tsx`
- **Catalog row:** #130 Switch (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2 (B1 council migration 2026-06-12; M1 superseded)

---

## 1. ARIA structure (M2)

| Attribute | Element | Value |
|---|---|---|
| `type="button"` | `<button>` | Native button (keyboard-focusable) |
| `role="switch"` | `<button>` | ARIA switch role |
| `aria-checked={isOn}` | `<button>` | Current on/off state |
| `aria-invalid={error}` | `<button>` | Error state (G-SW3 CLOSED) |
| `aria-required={required}` | `<button>` | Required state |
| `id` | `<button>` | For external label association |
| `disabled` | `<button>` | Disabled state (native semantics) |
| `type="hidden"` | `<input>` | Form participation (when `name` is set) |
| `name` | `<input>` | Form field name |
| `value="on"/"off"` | `<input>` | Always submits both states |

---

## 2. Label association

The label text (`label` / `onLabel` / `offLabel`) renders as a sibling `<span>` of the `<button>`. When `id` is provided, external `<label htmlFor>` can associate with the button element. The rendered text serves as an accessible description; for a formal accessible name, callers should use `aria-label` on the button via `className` prop pattern or the `id`+`<label>` approach.

---

## 3. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SW1 | Medium | Dual element approach (hidden checkbox + `role="switch"` span) may cause AT to announce the control twice | **CLOSED — M2 migration 2026-06-12** |
| G-SW3 | Medium | `aria-invalid` not supported — error state cannot be communicated to AT | **CLOSED — M2 migration 2026-06-12** |

All M1 gaps closed by B1 migration. M2 is a single `<button role="switch">` element; no dual-element announcement risk.
