# Highlight — Semantic Contract

- **Component:** Highlight
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Highlight.Interaction.md) · [Accessibility](./Highlight.Accessibility.md) · [Styling](./Highlight.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Highlight.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled text highlight wrapper

---

## 1. Purpose

The `badges/Highlight.tsx` module exports two related components:

- **`Highlight`** — a coloured inline text highlight rendered as a `<mark>` element. Used to draw attention to a word or phrase in running text.
- **`SearchHighlight`** — a convenience wrapper that parses a `text` string for occurrences of `query` and wraps each match with `<Highlight>`.

The `<mark>` element is the canonical HTML element for "marked or highlighted text" — it carries AT semantic meaning (screen readers may announce it). This distinguishes Highlight from a generic styled `<span>`.

---

## 2. Data model

### `Highlight`

```typescript
type HighlightColor = 'yellow' | 'green' | 'blue' | 'orange' | 'pink' | 'purple'

interface HighlightProps extends React.HTMLAttributes<HTMLElement> {
  color?: HighlightColor
}
```

### `SearchHighlight`

```typescript
interface SearchHighlightProps {
  text: string
  query: string
  color?: HighlightColor
  caseSensitive?: boolean
  className?: string
}
```

---

## 3. Props — `Highlight`

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `color` | `HighlightColor` | `'yellow'` | The background tint family: `yellow`, `green`, `blue`, `orange`, `pink`, `purple`. |
| `className` | `string` | — | Additional classes merged via `cn()`. |
| `children` | `ReactNode` | _required_ | The text (or arbitrary content) to highlight. |
| `...props` | `HTMLAttributes<HTMLElement>` | — | All other `<mark>` attributes passed through (e.g., `id`, `data-*`, `aria-*`). |

---

## 4. Props — `SearchHighlight`

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `text` | `string` | _required_ | The full text string to parse. |
| `query` | `string` | _required_ | The search term to highlight. If `query.trim()` is empty, returns the full text unstyled. |
| `color` | `HighlightColor` | `'yellow'` | Passed to each `<Highlight>` match. |
| `caseSensitive` | `boolean` | `false` | When `true`, matching is case-sensitive. |
| `className` | `string` | — | Applied to the outer `<span>` wrapper. |

### 4.1 `SearchHighlight` algorithm

1. If `query.trim()` is empty: return `<span className={className}>{text}</span>`.
2. Escape regex special chars in `query`.
3. Split `text` on `(escaped)` with flags `g` (or `gi` for case-insensitive).
4. For each part: if it equals `query` (per `caseSensitive`), wrap in `<Highlight>`. Otherwise render as plain text.

---

## 5. Events

`Highlight` passes through all `HTMLAttributes<HTMLElement>` events via `...props`. No events are defined by the component itself.

`SearchHighlight` has no events.

---

## 6. Slots

`Highlight` has one slot: `children` — the highlighted content.

`SearchHighlight` has no slots.

---

## 7. Component composition

- **DataGrid cells with search.** `<SearchHighlight text={cellValue} query={searchTerm} />` highlights search matches in-cell.
- **Autocomplete dropdowns.** Match highlighting in suggestion list items.
- **Inline editorial annotations.** Manual `<Highlight color="green">` for commentary markup.
- **Table row name columns.** `SearchHighlight` applied to entity names while a search is active.

---

## 8. Deferred features

- **Multi-query highlighting** — highlighting multiple different queries simultaneously in different colours. Deferred; `SearchHighlight` supports only a single `query`.
- **Highlight with tooltip** — annotated highlight that shows a popover on hover. Deferred; hosts compose Highlight inside their Tooltip primitive.
- **Regex query support** — passing a `RegExp` directly. Deferred; `query` is always a string.
