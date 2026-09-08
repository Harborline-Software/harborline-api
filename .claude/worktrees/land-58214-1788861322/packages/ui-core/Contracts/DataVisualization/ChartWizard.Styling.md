# ChartWizard — Styling Contract

- **Component:** ChartWizard
- **ADR 0017 family:** DataVisualization
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ChartWizard.Semantic.md) · [Interaction](./ChartWizard.Interaction.md) · [Accessibility](./ChartWizard.Accessibility.md) · [Styling](./ChartWizard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/charts/ChartWizard.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

`flex flex-col gap-2` + consumer `className`. No border, no background.

---

## 2. Toolbar container

`flex flex-wrap gap-1` — buttons wrap to multiple lines on narrow containers.

---

## 3. Type-selector buttons

| State | Classes |
|---|---|
| Active | `rounded px-3 py-1 text-sm bg-primary text-primary-foreground` |
| Inactive | `rounded px-3 py-1 text-sm bg-muted text-muted-foreground hover:bg-muted/80` |

No focus ring override; the browser default outline applies.

---

## 4. Chart area

`Chart` component renders below the toolbar with no additional wrapper styling. Width and height are passed directly to `Chart` via props.

---

## 5. Design tokens used

| Token | Usage |
|---|---|
| `bg-primary` / `text-primary-foreground` | Active type button background and text |
| `bg-muted` / `text-muted-foreground` | Inactive type button background and text |
| `hover:bg-muted/80` | Inactive button hover state |

Chart-internal tokens (`--chart-1` through `--chart-5`, `--border`, `--popover`, etc.) are delegated to the `Chart` component and its `buildBaseOption` layer.
