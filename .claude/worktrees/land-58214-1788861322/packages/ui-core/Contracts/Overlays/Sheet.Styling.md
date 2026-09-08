# Sheet — Styling Contract

- **Component:** Sheet
- **ADR 0017 family:** Overlays
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Sheet.Semantic.md) · [Interaction](./Sheet.Interaction.md) · [Accessibility](./Sheet.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A5 Sheet / SidePanel (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; shadcn Sheet baseline)

---

## 1. Overlay

`fixed inset-0 z-50 bg-black/80`

Animate in: `animate-in fade-in-0`

Animate out: `animate-out fade-out-0`

---

## 2. Content panel — base

`fixed z-50 gap-4 bg-background p-6 shadow-lg transition ease-in-out`

---

## 3. Content panel — side variants

| Side | Position classes | Animate in | Animate out |
|---|---|---|---|
| `right` (default) | `inset-y-0 right-0 h-full w-3/4 sm:max-w-sm` | `slide-in-from-right` | `slide-out-to-right` |
| `left` | `inset-y-0 left-0 h-full w-3/4 sm:max-w-sm` | `slide-in-from-left` | `slide-out-to-left` |
| `top` | `inset-x-0 top-0 border-b` | `slide-in-from-top` | `slide-out-to-top` |
| `bottom` | `inset-x-0 bottom-0 border-t` | `slide-in-from-bottom` | `slide-out-to-bottom` |

---

## 4. Sub-component styling

`SheetHeader`: `flex flex-col space-y-2 text-center sm:text-left`

`SheetFooter`: `flex flex-col-reverse sm:flex-row sm:justify-end sm:space-x-2`

`SheetTitle`: `text-lg font-semibold text-foreground`

`SheetDescription`: `text-sm text-muted-foreground`

`SheetClose` (X button): `absolute right-4 top-4 rounded-sm opacity-70 transition-opacity hover:opacity-100 focus:outline-none focus:ring-2 focus:ring-ring`

---

## 5. Design tokens

Uses design tokens throughout: `bg-background`, `text-foreground`, `text-muted-foreground`, `ring-ring`. No hardcoded colors.
