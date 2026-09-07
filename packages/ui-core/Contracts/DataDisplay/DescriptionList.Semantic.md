# DescriptionList — Semantic Contract

- **Component:** DescriptionList
- **ADR 0017 family:** DataDisplay
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DescriptionList.Interaction.md) · [Accessibility](./DescriptionList.Accessibility.md) · [Styling](./DescriptionList.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/DescriptionList.tsx`
- **Catalog row:** #A26 DescriptionList (`app-priority: medium`, `library-scope: v1`) — Harborline-native term/value definition list
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<dl>/<dt>/<dd>` wrapper

---

## 1. Component purpose

**DescriptionList** — renders a list of term/description pairs using the native `<dl>` element. Supports three layout modes (stacked, inline, grid), optional striping, and compact density. Delegates all term/description content to the caller via the `items` prop.

---

## 2. Props

```typescript
interface DescriptionListItem {
  term: React.ReactNode
  description: React.ReactNode
}

interface DescriptionListProps extends React.HTMLAttributes<HTMLDListElement> {
  items: DescriptionListItem[]
  layout?: 'stacked' | 'inline' | 'grid'   // default: 'stacked'
  columns?: 1 | 2 | 3                       // grid layout only; default: 2
  striped?: boolean                          // default: false
  compact?: boolean                          // default: false
}
```

All `HTMLDListElement` attributes are spread onto the `<dl>` element.

---

## 3. Layout modes

**stacked** (default): Each item is a `<div>` containing `<dt>` above `<dd>`. Items separated by dividers.

**inline**: Each item is a `<div>` with `<dt>` and `<dd>` side by side. Term has fixed width.

**grid**: Items laid out in a CSS grid with `columns` controlling column count (responsive: `sm:grid-cols-{N}`).

---

## 4. Striping

When `striped=true`, alternating items receive a background fill (`bg-gray-50`). Applies in all layout modes.

---

## 5. Compact mode

When `compact=true`, reduces vertical padding on items.
