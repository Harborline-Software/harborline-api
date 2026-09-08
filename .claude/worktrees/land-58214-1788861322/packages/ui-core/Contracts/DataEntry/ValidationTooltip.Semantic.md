# ValidationTooltip — Semantic Contract

- **Component:** ValidationTooltip
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ValidationTooltip.Interaction.md) · [Accessibility](./ValidationTooltip.Accessibility.md) · [Styling](./ValidationTooltip.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Telerik UI ValidationTooltip)
- **Catalog row:** #147 ValidationTooltip (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik ValidationTooltip baseline)

---

## 1. Component purpose

**ValidationTooltip** — an overlay tooltip variant that displays field validation messages adjacent to the invalid input element, rather than below it in a `ValidationMessage`. Used when vertical space is constrained (e.g., dense form layouts) or when inline error messages would disrupt layout flow.

---

## 2. Props (planned)

```typescript
interface ValidationTooltipProps {
  for: string | React.RefObject<HTMLElement>   // ID or ref of the associated input
  messages: string | string[]                   // validation message(s) to display
  show?: boolean                                // controlled visibility; default: auto (shows when messages non-empty)
  position?: 'top' | 'right' | 'bottom' | 'left'  // default: 'right'
  offset?: number                               // px offset from input; default: 8
  className?: string
}
```

---

## 3. Relationship to ValidationMessage

`ValidationMessage` renders inline in document flow below the input. `ValidationTooltip` renders via a portal positioned absolutely adjacent to the input. Use ValidationTooltip when the form layout cannot accommodate the vertical space of an inline error.

---

## 4. Auto-show behavior

When `show` is not provided, the tooltip shows automatically when `messages` is non-empty and hides when empty.
