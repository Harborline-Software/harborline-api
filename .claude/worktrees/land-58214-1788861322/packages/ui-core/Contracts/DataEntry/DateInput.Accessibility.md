# DateInput — Accessibility Contract

- **Component:** DateInput
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DateInput.Semantic.md) · [Interaction](./DateInput.Interaction.md) · [Styling](./DateInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateInput.tsx`
- **Catalog row:** #37 DateInput (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

The component renders a single `<input type="date">`. No additional ARIA attributes are added beyond what the native element provides.

| Attribute | Element | Value |
|---|---|---|
| `id` | `<input>` | For `<label htmlFor>` association |
| `disabled` | `<input>` | Disabled state |
| `readOnly` | `<input>` | Readonly state |
| `min` / `max` | `<input>` | Native constraint |
| `name` | `<input>` | Form participation |

Callers must supply an external `<label htmlFor={id}>` or `aria-label`.

---

## 2. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DI2 | Medium | `aria-invalid` not set — error state not communicated to AT | Accepted-risk M1 |
| G-DI3 | Low | No `aria-describedby` — hint/error text cannot be linked | Accepted-risk M1 |
| G-DI5 | Low | Browser date picker accessibility varies significantly across platforms and browsers | Accepted-risk M1 |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (required → aria-required), FR-2 (focus events).
> Supersedes: G-DI2 (aria-invalid) and G-DI3 (aria-describedby) are RESOLVED in Wave-N — see below.

### 3. Expanded ARIA attributes (Wave-N)

| Attribute | Element | Value | Note |
|---|---|---|---|
| `aria-required` | `<input>` | `"true"` when `required` prop is set | FR-1 §1 |
| `aria-invalid` | `<input>` | `"true"` when `error` context signals invalid state | Resolves G-DI2 |
| `aria-describedby` | `<input>` | `ariaDescribedBy` prop value | Resolves G-DI3 — links hint/error text |
| `aria-labelledby` | `<input>` | `ariaLabelledBy` prop value | Audit miss — replaces/augments external label |
| `aria-label` | `<input>` | `ariaLabel` prop value | Fallback when no visible label |

`aria-invalid` is set when the component receives `error=true` via `FormFieldContext` (FR-1 §4 context integration) OR when a future `error` prop is set directly. The component does not validate itself — it reflects the error signal from context.

### 4. Spinner buttons (segmented-editor wave)

When spinners are rendered: each spin button carries `aria-label="Increment {segment}"` / `aria-label="Decrement {segment}"`. The segment container uses `role="spinbutton"` with `aria-valuenow`, `aria-valuemin`, `aria-valuemax`, and `aria-valuetext` per the ARIA spinbutton pattern.
