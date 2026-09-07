# CheckBox — Styling Contract

- **Component:** CheckBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CheckBox.Semantic.md) · [Interaction](./CheckBox.Interaction.md) · [Accessibility](./CheckBox.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/CheckBox.tsx`
- **Catalog row:** #24 Checkbox (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Input classes

Base: `cursor-pointer accent-primary`

Disabled: adds `cursor-not-allowed opacity-50`

Size classes:

| Size | Class |
|---|---|
| `small` | `h-3.5 w-3.5` |
| `medium` | `h-4 w-4` |
| `large` | `h-5 w-5` |

> **M1 note:** Color uses `accent-primary` design token (not hardcoded). Browser-default checkbox appearance is used; no custom SVG tick.

---

## 2. Label wrapper (when `label` provided)

`inline-flex items-center gap-2 cursor-pointer select-none`

Label size:

| Size | Class |
|---|---|
| `small` | `text-sm` |
| `medium` | `text-sm` |
| `large` | `text-base` |

Disabled: adds `cursor-not-allowed opacity-50`

---

## 3. No-label wrapper

`<span className={className}>` — passes `className` through to a span wrapper only.

---

## 4. className passthrough

`className` applies to the outer container (`<label>` or `<span>`), not the input directly.
