# PropertyCard — Styling Contract

- **Component:** PropertyCard
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PropertyCard.Semantic.md) · [Interaction](./PropertyCard.Interaction.md) · [Accessibility](./PropertyCard.Accessibility.md) · [Styling](./PropertyCard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/PropertyCard.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Card root

```
rounded-lg border border-border bg-card p-4 shadow-sm
```

---

## 2. Header row

```
flex items-start justify-between gap-2
```

### Address text

```
truncate text-sm font-medium text-card-foreground
```

Truncated with CSS ellipsis.

### City/state text

```
text-xs text-muted-foreground
```

---

## 3. Status badge

```
shrink-0 rounded-full px-2 py-0.5 text-xs font-medium
```

Plus colour variant class (see §5).

---

## 4. Bottom row

```
mt-3 flex items-center justify-between
```

- Unit count: `text-xs text-muted-foreground`
- Property name: `font-mono text-xs text-muted-foreground`

---

## 5. Status colour variants

| `status` | Tailwind class |
|---|---|
| `'Active'` | `bg-success/15 text-success` |
| `'Vacant'` | `bg-warning/15 text-warning` |
| `'Maintenance'` | `bg-priority-medium text-priority-medium-fg` |
| `'Sold'` | `bg-muted text-muted-foreground` |
| Other | `bg-muted text-muted-foreground` |

These colour tokens (`bg-success`, `bg-warning`, `bg-priority-medium`) are semantic aliases from the design token system. They adapt to dark mode if the provider theme supplies dark-mode token values.

---

## 6. Actions footer

```
mt-3 border-t border-border pt-3
```

Rendered only when `actions` prop is provided.

---

## 7. Token usage

PropertyCard uses design token aliases extensively:

| Tailwind utility | Semantic token |
|---|---|
| `border-border` | `--border` |
| `bg-card` | `--card` |
| `text-card-foreground` | `--card-foreground` |
| `text-muted-foreground` | `--muted-foreground` |
| `bg-muted` | `--muted` |
| `text-success` | `--success` |
| `bg-success/15` | `--success` at 15% opacity |
| `text-warning` | `--warning` |
| `bg-warning/15` | `--warning` at 15% opacity |

---

## 8. Visual states

PropertyCard has one visual state per status value. No hover, focus, active, or selected state is built into the component.

---

## 9. Dark mode

Token-based colours (`--card`, `--border`, `--success`, etc.) adapt automatically to dark mode via provider theme. Direct Tailwind colour classes are not used.

---

## 10. Responsive behaviour

The card is a fixed layout — no responsive breakpoints. In grid layouts, the host controls card sizing via the grid container.
