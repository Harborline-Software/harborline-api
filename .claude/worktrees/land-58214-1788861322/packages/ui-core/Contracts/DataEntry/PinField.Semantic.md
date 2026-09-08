# PinField — Semantic Contract

- **Component:** PinField
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./PinField.Interaction.md) · [Accessibility](./PinField.Accessibility.md) · [Styling](./PinField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/PinField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled PIN entry (multi-cell)

---

## 1. Purpose

PinField is a multi-cell PIN or one-time-code entry control. It renders `length`
individual single-character input boxes in a row, handles focus advancement on
character entry, focus retreat on backspace, arrow-key navigation, and paste.
Typed characters are automatically uppercased for `'alphanumeric'` mode.

---

## 2. Data model

PinField is fully controlled. The host owns the full PIN string.

```typescript
type PinFieldType = 'numeric' | 'alphanumeric'

interface PinFieldProps {
  length?: number           // number of cells; default 6
  value: string             // the current PIN string; padded/truncated to `length`
  onChange: (value: string) => void  // fires with the new full PIN string
  type?: PinFieldType       // default 'numeric'
  disabled?: boolean
  error?: boolean
  'aria-label'?: string     // group and individual cell label base; default 'PIN'
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `length` | `number` | `6` | Number of character cells rendered. |
| `value` | `string` | required | Full controlled PIN string. Characters beyond `length` are ignored; shorter strings are padded with empty cells. |
| `onChange` | `(value: string) => void` | required | Fires with the new full-length (or shorter) PIN string on any change. |
| `type` | `PinFieldType` | `'numeric'` | `'numeric'`: only `[0-9]` allowed; `inputMode="numeric"`. `'alphanumeric'`: `[0-9a-zA-Z]` allowed; `inputMode="text"`; characters uppercased. |
| `disabled` | `boolean` | `false` | Disables all cells. |
| `error` | `boolean` | `false` | When `true`, applies error border and ring to all cells. |
| `aria-label` | `string` | `'PIN'` | Base label for the group (`role="group"`) and each cell (`"PIN digit N"`). |
| `className` | `string` | `undefined` | Merged onto the root `<div>`. |

### 3.1 Value representation

The `value` string maps character-by-character to cell indexes. `value[i]` is
displayed in cell `i`. Empty cells display as blank (empty string).

### 3.2 Character case

For `type === 'alphanumeric'`, characters are uppercased via
`.toUpperCase()` before being stored. Paste is also uppercased and truncated.

### 3.3 FormField integration

PinField does NOT consume `FormFieldContext`. No `id` from context; no
`aria-describedby` wiring.

---

## 4. Events

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `(value: string)` | Character typed, deleted (backspace), or pasted. Payload is the full current PIN string (empty cells represented by `''` are joined — the string length may be shorter than `length`). |

---

## 5. Deferred / known gaps

| Gap | Description |
|---|---|
| S-1 | No FormField integration |
| S-2 | No `aria-describedby` or error message text |
| S-3 | `onChange` string may be shorter than `length` when trailing cells are empty (padded with empty strings that join to nothing) |
| S-4 | No `size` prop — single fixed cell size (h-12 w-10) |
