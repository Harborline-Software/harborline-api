# ToggleGroup — Semantic Contract

- **Component:** ToggleGroup
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ToggleGroup.Interaction.md) · [Accessibility](./ToggleGroup.Accessibility.md) · [Styling](./ToggleGroup.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ToggleGroup.tsx`
- **Catalog row:** #A14 ToggleGroup (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled toggle button group

---

## 1. Component purpose

**ToggleGroup** — a connected row of toggle buttons where exactly one (`type="single"`) or any number (`type="multiple"`) can be active at a time. Common uses: text-alignment controls, view-mode switchers, filter tag sets.

---

## 2. Props

```typescript
type ToggleGroupType = 'single' | 'multiple'
type ToggleGroupSize = 'sm' | 'md' | 'lg'       // NOTE: abbreviated scale, not small/medium/large
type ToggleGroupVariant = 'outline' | 'ghost'

interface ToggleGroupOption {
  value: string
  label: React.ReactNode
  disabled?: boolean
  'aria-label'?: string
}

// Discriminated union: type drives value/onValueChange types

interface ToggleGroupSingle {
  type: 'single'
  value: string
  onValueChange: (value: string) => void
}

interface ToggleGroupMultiple {
  type: 'multiple'
  value: string[]
  onValueChange: (value: string[]) => void
}

type ToggleGroupBaseProps = {
  options: ToggleGroupOption[]
  size?: ToggleGroupSize          // default: 'md'
  variant?: ToggleGroupVariant    // default: 'outline'
  disabled?: boolean              // disables all buttons
  'aria-label'?: string           // names the group for AT
  className?: string
}

type ToggleGroupProps = ToggleGroupBaseProps & (ToggleGroupSingle | ToggleGroupMultiple)
```

---

## 3. Controlled-only

ToggleGroup is fully **controlled** — there is no `defaultValue`. The parent always provides `value` and `onValueChange`. The component has no internal state.

---

## 4. Selection semantics

| `type` | `value` type | `onValueChange` type |
|---|---|---|
| `single` | `string` | `(value: string) => void` |
| `multiple` | `string[]` | `(value: string[]) => void` |

Single selection does NOT deselect on re-click of the active item (no empty-string emission — the clicked value is always emitted, same as current).

Multiple selection toggles each item independently.

---

## 5. Size scale note

ToggleGroup uses abbreviated size tokens: `'sm' | 'md' | 'lg'` — not `'small' | 'medium' | 'large'` as most other components do. This is an M1 API inconsistency.

---

## 6. Aliases

The following catalog entries redirect to ToggleGroup — they have no separate implementation:

- **[ToggleButton](./ToggleButton.Semantic.md)** — single independent toggle button (ToggleGroup with one option)
- **[ButtonGroup](./ButtonGroup.Semantic.md)** — connected action button strip (ToggleGroup with no selection)
- **[SegmentedControl](./SegmentedControl.Semantic.md)** — iOS-style mutually exclusive strip (ToggleGroup `type="single"`)
