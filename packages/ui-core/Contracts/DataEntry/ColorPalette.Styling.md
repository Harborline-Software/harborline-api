# ColorPalette — Styling Contract

- **Component:** ColorPalette
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColorPalette.Semantic.md) · [Interaction](./ColorPalette.Interaction.md) · [Accessibility](./ColorPalette.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ColorPalette.tsx`
- **Catalog row:** #30 ColorPalette (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`inline-grid gap-0.5`
Grid template: `style={{ gridTemplateColumns: 'repeat({columns}, {tileSize}px)' }}`
Disabled: `opacity-50 pointer-events-none`

---

## 2. Each swatch

`rounded-sm transition-transform hover:scale-110 focus:outline-none focus:ring-2 focus:ring-ring`

| State | Classes |
|---|---|
| Default | — |
| Selected | `ring-2 ring-primary ring-offset-1` |

Background set via inline `style={{ width: tileSize, height: tileSize, background: color }}`.
