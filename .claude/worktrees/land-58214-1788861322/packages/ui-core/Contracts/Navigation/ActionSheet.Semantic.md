# ActionSheet — Semantic Contract

- **Component:** ActionSheet
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ActionSheet.Interaction.md) · [Accessibility](./ActionSheet.Accessibility.md) · [Styling](./ActionSheet.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/ActionSheet.tsx`
- **Catalog row:** #1 ActionSheet (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled bottom sheet (mobile-style)

---

## 1. Component purpose

**ActionSheet** — a mobile-pattern bottom sheet that presents a list of contextual actions to the user. Slides in from the bottom of the viewport with a backdrop. Each action item can have an icon, and destructive items are styled in the danger color. A separate cancel button is always shown.

---

## 2. Props

```typescript
interface ActionSheetItem {
  text: string
  icon?: React.ReactNode
  onClick?: () => void
  disabled?: boolean
  destructive?: boolean        // applies danger styling
}

interface ActionSheetProps {
  open?: boolean               // controlled
  defaultOpen?: boolean        // default: false
  onOpenChange?: (open: boolean) => void
  items: ActionSheetItem[]     // required; action list
  title?: string               // optional header above items
  cancelText?: string          // default: 'Cancel'
  onCancel?: () => void        // fires when cancel is triggered
  className?: string
}
```

---

## 3. Open/closed model

`open` / `defaultOpen` control visibility. When uncontrolled, internal state manages `isOpen`. When controlled, `open` is the source of truth. `onOpenChange` fires on all state transitions.

---

## 4. Cancel behavior

Cancel can be triggered by:
1. Clicking the cancel button
2. Clicking the backdrop

Both fire `onCancel()` (if provided) and call `setOpen(false)`.

---

## 5. Item structure

Items are rendered in one card; the cancel button is in a separate card below. When `title` is provided, it appears as a muted header at the top of the items card.

---

## 6. Destructive items

Items with `destructive: true` render in `text-destructive` color with a hover background in the destructive tint, visually signaling irreversible or danger actions.
