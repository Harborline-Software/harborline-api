# CommandPalette — Semantic Contract

- **Component:** CommandPalette
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./CommandPalette.Interaction.md) · [Accessibility](./CommandPalette.Accessibility.md) · [Styling](./CommandPalette.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/CommandPalette.tsx`
- **Catalog row:** #A1 CommandPalette (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled search-input + results list

---

## 1. Component purpose

**CommandPalette** — a full-screen modal search interface for quickly finding and activating commands. Items can be grouped, have icons and descriptions, and are searchable by label, description, and keywords.

---

## 2. Props

```typescript
interface CommandItem {
  id: string
  label: string
  description?: string
  icon?: React.ReactNode
  group?: string             // groups items under a header
  keywords?: string[]        // additional search terms
  onSelect: () => void       // required; fired when item activated
}

interface CommandPaletteProps {
  open: boolean              // required; controlled-only
  onOpenChange: (open: boolean) => void  // required
  items: CommandItem[]       // required
  placeholder?: string       // default: 'Search commands…'
}
```

> **Controlled-only:** `CommandPalette` has no `defaultOpen` prop. The parent must control open state entirely.

---

## 3. Search model

Filtering is case-insensitive substring matching against:
- `item.label`
- `item.description` (if present)
- `item.keywords` (if present; any keyword matching the query passes)

An empty query shows all items.

---

## 4. Grouping

Items with the same `group` string are rendered under a non-interactive group header. Items with no `group` form an implicit ungrouped group. Group order follows insertion order in the `items` array.

---

## 5. Active index

A flat index (`activeIndex`) tracks keyboard focus across all filtered items regardless of grouping. `activeIndex` resets to 0 when the query changes or when the palette opens.
