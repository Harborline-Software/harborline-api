# SplitButton — Semantic Contract

- **Component:** SplitButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./SplitButton.Interaction.md) · [Accessibility](./SplitButton.Accessibility.md) · [Styling](./SplitButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/SplitButton.tsx`
- **Catalog row:** #124 SplitButton (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled split button (primary + dropdown)

---

## 1. Component purpose

**SplitButton** — a two-part button: a primary action button on the left and a caret button on the right that opens a dropdown of additional options. The two parts share size and variant but have independent click handlers.

---

## 2. Props

```typescript
interface SplitButtonOption {
  label: string
  onClick: () => void
  disabled?: boolean
}

interface SplitButtonProps {
  label: string                            // required; primary action label
  onClick: () => void                      // required; primary action handler
  options: SplitButtonOption[]             // required; dropdown options
  variant?: 'primary' | 'default'         // default: 'default'
  size?: 'sm' | 'md' | 'lg'              // default: 'md'
  disabled?: boolean                       // default: false
  loading?: boolean                        // default: false
  className?: string
}
```

> **M1 API note:** `size` uses abbreviated tokens (`'sm'|'md'|'lg'`) rather than the long form (`'small'|'medium'|'large'`) used by other components such as DropDownButton. This inconsistency is an M1-era artifact.

---

## 3. Two-part structure

The primary button renders `label` (or a spinner when `loading=true`) and fires `onClick`. The caret button is a narrow icon-only button that opens/closes the dropdown. The two buttons are adjacent with no gap and share a visual boundary between them.

---

## 4. Loading state

When `loading=true`:
- Primary button shows an animated spinner SVG in place of `label`
- Both primary and caret buttons are disabled
- `onClick` does not fire

---

## 5. Variant

`'primary'` renders both parts with a blue accent background (hardcoded in M1, not using the theme token system). `'default'` renders with a neutral gray surface. This is a known M1 deviation from the Harborline token system — see Styling contract §4.
