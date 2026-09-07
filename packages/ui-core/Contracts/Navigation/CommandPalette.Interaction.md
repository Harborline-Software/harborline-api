# CommandPalette — Interaction Contract

- **Component:** CommandPalette
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CommandPalette.Semantic.md) · [Accessibility](./CommandPalette.Accessibility.md) · [Styling](./CommandPalette.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/CommandPalette.tsx`
- **Catalog row:** #A1 CommandPalette (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
CLOSED (open=false)
  → open=true → OPEN (query reset to '', activeIndex=0, input auto-focused after 50ms)

OPEN (open=true)
  → type in input → filter items; reset activeIndex to 0
  → ArrowDown → activeIndex++ (clamped at filtered.length-1); scroll active into view
  → ArrowUp → activeIndex-- (clamped at 0); scroll active into view
  → Enter → filtered[activeIndex].onSelect() → CLOSED
  → click item → item.onSelect() → CLOSED
  → hover item → setActiveIndex(item's flat index)
  → Escape → CLOSED
  → click backdrop → CLOSED
```

---

## 2. Focus behavior

On open: `inputRef.current?.focus()` called after 50ms `setTimeout` (allows render to complete). This means there is a brief moment before focus lands in the input.

Input is always focused while the palette is open. Arrow keys and Enter fire from the input's `onKeyDown`.

---

## 3. Active item scroll

After `activeIndex` changes, the component calls `el?.scrollIntoView({ block: 'nearest' })` on the active `[data-active="true"]` element to keep it visible.

---

## 4. Close triggers

1. Escape key (from input `onKeyDown`)
2. Enter key activating an item
3. Click on item
4. Click on backdrop (`aria-hidden` overlay)

---

## 5. No results

When `filtered.length === 0`, a "No results" message is shown inside the list. The input remains active.

---

## 6. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-CPAL1 | Low | 50ms setTimeout for focus is a fragile timing assumption | Accepted-risk M1; works in practice; RAF-based approach deferred |
