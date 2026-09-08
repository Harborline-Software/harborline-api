# SmartPasteButton — Styling Contract

- **Component:** SmartPasteButton
- **ADR 0017 family:** AI
- **Contract type:** Styling
- **Status:** Draft (forward-spec; no implementation — promote to Accepted when PR opens)
- **Companion contracts:** [Semantic](./SmartPasteButton.Semantic.md) · [Interaction](./SmartPasteButton.Interaction.md) · [Accessibility](./SmartPasteButton.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A28 SmartPasteButton (`app-priority: low`, `library-scope: future-wave`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik SmartPasteButton baseline)

---

## 1. Root button

`inline-flex items-center gap-1.5 h-9 px-4 rounded-md text-sm font-medium border border-border bg-background text-foreground hover:bg-muted hover:text-foreground focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2 disabled:opacity-50 disabled:pointer-events-none transition-colors`.

AI indicator icon (sparkle/paste): `h-4 w-4 text-primary shrink-0`.

---

## 2. Loading state

When `loading=true`: add `opacity-70 cursor-wait`. Optionally replace the AI icon with a spinner: `animate-spin h-4 w-4 border-2 border-current border-t-transparent rounded-full`.

---

## 3. Design tokens

Uses: `hsl(var(--background))`, `hsl(var(--foreground))`, `hsl(var(--border))`, `hsl(var(--muted))`, `hsl(var(--primary))`, `hsl(var(--ring))`.
