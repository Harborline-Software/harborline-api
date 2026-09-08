# ValidationTooltip — Styling Contract

- **Component:** ValidationTooltip
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ValidationTooltip.Semantic.md) · [Interaction](./ValidationTooltip.Interaction.md) · [Accessibility](./ValidationTooltip.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #147 ValidationTooltip (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik ValidationTooltip baseline)

---

## 1. Tooltip panel

`absolute z-50 max-w-[200px] rounded-md bg-destructive px-3 py-2 text-sm text-destructive-foreground shadow-sm`

---

## 2. Arrow indicator

Small CSS triangle pointing toward the associated input. Direction based on `position` prop.

---

## 3. Animation

`animate-in fade-in-0 zoom-in-95 duration-100`

---

## 4. Design tokens

Uses design tokens: `bg-destructive`, `text-destructive-foreground` — consistent with other validation error styling.
