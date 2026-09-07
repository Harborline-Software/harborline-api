# MaskedTextBox — Semantic Contract

- **Component:** MaskedTextBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./MaskedTextBox.Interaction.md) · [Accessibility](./MaskedTextBox.Accessibility.md) · [Styling](./MaskedTextBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/MaskedTextBox.tsx`
- **Catalog row:** #82 MaskedTextBox (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled input with mask formatting

---

## 1. Component purpose

**MaskedTextBox** — a text input that formats its value according to a mask string. The mask defines literal characters and placeholder positions for digits. Shows a formatted display value with `promptChar` placeholders for unfilled positions.

---

## 2. Props

```typescript
interface MaskedTextBoxProps {
  value?: string            // controlled raw value (digits only)
  defaultValue?: string     // default: ''
  onChange?: (value: string, rawValue: string) => void
                            // first arg: masked display string; second arg: raw digits
  mask: string              // required; mask pattern (see §3)
  placeholder?: string      // overrides auto-generated mask placeholder
  promptChar?: string       // default: '_'; character for unfilled mask positions
  disabled?: boolean        // default: false
  readonly?: boolean        // default: false
  size?: 'small' | 'medium' | 'large'        // default: 'medium'
  fillMode?: 'solid' | 'outline' | 'flat'    // default: 'solid'
  rounded?: 'small' | 'medium' | 'large' | 'full'  // default: 'medium'
  id?: string
  name?: string
  className?: string
}
```

---

## 3. Mask tokens

| Token | Matches |
|---|---|
| `0` | Required digit |
| `L` | Letter placeholder (M1: renders promptChar; no letter-only validation) |
| `A` | Alphanumeric placeholder (M1: renders promptChar; no alphanumeric validation) |
| Any other char | Literal (e.g. `-`, `(`, `)`, `/`) |

**M1 limitation:** `applyMask` strips all non-digits from the raw value before processing. `L` and `A` tokens behave the same as unrecognized: replaced with `promptChar`. Only digit extraction is supported.

---

## 4. Display vs raw value

- **Internal raw value**: digits only (e.g. `"5551234567"`)
- **Display value**: formatted (e.g. `"(555) 123-4567"`)

`onChange(maskedValue, rawValue)` — host receives both; store `rawValue` for data, display `maskedValue` for UX.

---

## 5. Placeholder

Auto-generated from mask: `mask.replace(/[0LA]/g, promptChar)` → e.g. mask `"(000) 000-0000"` → placeholder `"(___) ___-____"`.
