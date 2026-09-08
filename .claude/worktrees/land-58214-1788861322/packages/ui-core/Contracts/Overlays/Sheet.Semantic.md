# Sheet — Semantic Contract

- **Component:** Sheet
- **ADR 0017 family:** Overlays
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Sheet.Interaction.md) · [Accessibility](./Sheet.Accessibility.md) · [Styling](./Sheet.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: shadcn Sheet / Radix Dialog)
- **Catalog row:** #A5 Sheet / SidePanel (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; shadcn Sheet baseline)

---

## 1. Component purpose

**Sheet** (alias: SidePanel) — a panel that slides in from one edge of the viewport (top, right, bottom, or left). Used for secondary task flows, detail views, and settings panels. Built on Radix Dialog primitives. Distinct from Drawer (#46) in that Drawer is for primary navigation contexts; Sheet is for task-level overlays.

---

## 2. Compound component API (planned)

```typescript
// Root — Radix Dialog.Root
interface SheetProps {
  open?: boolean
  defaultOpen?: boolean
  onOpenChange?: (open: boolean) => void
  modal?: boolean   // default: true
}

// Trigger — Radix Dialog.Trigger
interface SheetTriggerProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  asChild?: boolean
}

// Content — Radix Dialog.Content wrapped with edge positioning
interface SheetContentProps {
  side?: 'top' | 'right' | 'bottom' | 'left'  // default: 'right'
  className?: string
  children?: React.ReactNode
}

// Header, Footer, Title, Description — layout sub-components
interface SheetHeaderProps extends React.HTMLAttributes<HTMLDivElement> {}
interface SheetFooterProps extends React.HTMLAttributes<HTMLDivElement> {}
interface SheetTitleProps extends React.HTMLAttributes<HTMLHeadingElement> {}
interface SheetDescriptionProps extends React.HTMLAttributes<HTMLParagraphElement> {}
interface SheetCloseProps extends React.ButtonHTMLAttributes<HTMLButtonElement> {
  asChild?: boolean
}
```

---

## 3. Overlay

Renders a semi-transparent backdrop (`SheetOverlay`) behind the panel that dismisses the Sheet on click when `modal=true`.

---

## 4. Content areas

Sub-components: `SheetHeader`, `SheetFooter`, `SheetTitle`, `SheetDescription`, `SheetClose`. These are layout/typography wrappers; callers use them to structure sheet content consistently.
