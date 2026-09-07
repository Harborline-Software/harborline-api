# Popup — Semantic Contract

- **Component:** Popup
- **ADR 0017 family:** Overlays
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Popup.Interaction.md) · [Accessibility](./Popup.Accessibility.md) · [Styling](./Popup.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Popup.tsx`
- **Catalog row:** #99 Popup (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled positioned `<div>` overlay

---

## 1. Component purpose

**Popup** — a low-level positioned overlay that renders via `ReactDOM.createPortal` into `document.body`. Positions itself relative to an anchor element using `getBoundingClientRect()`. Used as a building block for tooltips, dropdowns, and other anchor-relative content.

---

## 2. Props

```typescript
interface PopupProps {
  anchor: React.RefObject<HTMLElement | null> | HTMLElement | null
  open: boolean
  onOpenChange?: (open: boolean) => void
  offset?: { horizontal?: number; vertical?: number }
  popupAlign?: { horizontal?: 'left' | 'center' | 'right'; vertical?: 'top' | 'center' | 'bottom' }
  anchorAlign?: { horizontal?: 'left' | 'center' | 'right'; vertical?: 'top' | 'center' | 'bottom' }
  animate?: boolean          // default: true
  children: React.ReactNode
  className?: string
}
```

Defaults: `popupAlign: { horizontal: 'left', vertical: 'top' }`, `anchorAlign: { horizontal: 'left', vertical: 'bottom' }`

---

## 3. Portal rendering

When `open=true` and position is computed, renders content via `ReactDOM.createPortal(content, document.body)`. Returns `null` when closed or when position has not yet been computed.

---

## 4. Position computation

Runs in a `useEffect` triggered by `open`, `anchor`, `anchorAlign`, and `offset` changes:

1. Resolves anchor: `anchor instanceof HTMLElement ? anchor : anchor?.current`
2. Gets `getBoundingClientRect()` → converts to page coordinates: `rect.left + scrollX`, `rect.bottom + scrollY`
3. `anchorAlign.horizontal` determines the X attachment point: `left` → `rect.left`, `center` → `rect.left + width/2`, `right` → `rect.right`
4. `anchorAlign.vertical` determines the Y attachment point: `top` → `rect.top`, `center` → `rect.top + height/2`, `bottom` → `rect.bottom`
5. Adds `offset.horizontal` and `offset.vertical`

---

## 5. Popup alignment offset

CSS `transform: translate(transformX, transformY)` adjusts the popup's own origin:

- `popupAlign.horizontal='right'` → `-100%`; `'center'` → `-50%`; `'left'` → `'0'`
- `popupAlign.vertical='bottom'` → `-100%`; `'center'` → `-50%`; `'top'` → `'0'`
