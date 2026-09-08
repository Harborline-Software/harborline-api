# Separator — Semantic Contract

- **Component:** Separator
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Separator.Interaction.md) · [Accessibility](./Separator.Accessibility.md) · [Styling](./Separator.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Separator.tsx`
- **Catalog row:** #A13 Separator (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<hr>` or `<div>` with border styling

---

## 1. Component purpose

Visual divider between sections. Supports horizontal and vertical orientations, decorative (no semantic role) or semantic (role=separator), and an optional centered label variant.

---

## 2. Props

```typescript
type SeparatorOrientation = 'horizontal' | 'vertical'

interface SeparatorProps extends HTMLAttributes<HTMLDivElement> {
  orientation?: SeparatorOrientation   // default: 'horizontal'
  decorative?: boolean                  // default: true
  label?: string                        // centered label text; renders split-line variant
}
```

---

## 3. Rendering modes

**Plain separator** (`label` absent): renders a single `<div>` with CSS border/background.

**Labeled separator** (`label` present): renders a flex row with two half-lines and the label centered between them:
```
──────── Label ────────
```

---

## 4. Decorative vs semantic

When `decorative=true` (default): `role="none"` — AT ignores the divider.

When `decorative=false`: `role="separator"` + `aria-orientation` — AT announces the separator as a structural boundary.

Most separators between sections are decorative. Use `decorative=false` only when the separator has structural meaning (e.g., separating two distinct regions in a panel).
