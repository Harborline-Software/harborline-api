# FlatColorPicker — Styling Contract

- **Component:** FlatColorPicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FlatColorPicker.Semantic.md) · [Interaction](./FlatColorPicker.Interaction.md) · [Accessibility](./FlatColorPicker.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FlatColorPicker.tsx`
- **Catalog row:** #62 FlatColorPicker (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Panel container

`inline-flex flex-col border border-border rounded-md overflow-hidden`

---

## 2. Tab bar (when views.length > 1)

Container: `flex border-b border-border`

| Tab state | Classes |
|---|---|
| Active | `flex-1 text-xs py-1.5 capitalize bg-muted font-medium` |
| Inactive | `flex-1 text-xs py-1.5 capitalize hover:bg-muted/50` |

---

## 3. View content area

`p-3` wrapper around the active view (ColorGradient or ColorPalette).

---

## 4. Actions bar (showActions=true)

Container: `flex gap-2 px-3 pb-3`

| Button | Classes |
|---|---|
| Apply | `flex-1 text-xs py-1 bg-primary text-primary-foreground rounded hover:bg-primary/90` |
| Cancel | `flex-1 text-xs py-1 border border-input rounded hover:bg-muted` |
