# ActionSheet — Styling Contract

- **Component:** ActionSheet
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ActionSheet.Semantic.md) · [Interaction](./ActionSheet.Interaction.md) · [Accessibility](./ActionSheet.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/ActionSheet.tsx`
- **Catalog row:** #1 ActionSheet (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Backdrop

`fixed inset-0 z-40 bg-black/40`

---

## 2. Sheet container

`fixed bottom-0 left-0 right-0 z-50 flex flex-col gap-2 p-3 pb-safe-bottom`
`animate-in slide-in-from-bottom-4 duration-200`

---

## 3. Items card

`rounded-xl bg-background overflow-hidden shadow-lg`

---

## 4. Title (when provided)

`px-4 py-3 text-center text-sm font-medium text-muted-foreground border-b border-border`

---

## 5. Item button — base

`flex items-center gap-3 w-full px-4 py-3.5 text-sm font-medium text-left`
`transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring`
`disabled:opacity-50 disabled:cursor-not-allowed`

Separator between items: `border-t border-border/50` (applied on items at index > 0)

---

## 6. Item button — variants

| State | Classes |
|---|---|
| Default | `hover:bg-accent` |
| Destructive | `text-destructive hover:bg-destructive/10` |

---

## 7. Cancel card + button

Card: `rounded-xl bg-background overflow-hidden shadow-lg`
Button: `w-full px-4 py-3.5 text-sm font-semibold text-center hover:bg-accent transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring`
