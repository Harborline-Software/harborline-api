# Splitter — Interaction Contract

- **Component:** Splitter
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Splitter.Semantic.md) · [Accessibility](./Splitter.Accessibility.md) · [Styling](./Splitter.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Splitter.tsx`
- **Catalog row:** #125 Splitter (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. State machine

```
IDLE
  → pointerdown on divider[i] → DRAGGING(i, startPos, startSizes)

DRAGGING(i, startPos, startSizes)
  → pointermove → update sizes[i] and sizes[i+1] based on delta
  → pointerup → IDLE
```

---

## 2. Drag mechanics

The divider uses pointer capture (`setPointerCapture`) to ensure smooth dragging even when the cursor leaves the divider element. Pointer capture routes all subsequent `pointermove`/`pointerup` events directly to the capturing element — **do NOT also register `window.addEventListener('pointermove', ...)`**. Using both causes the handler to fire twice per event (once via capture, once via window bubbling), doubling every drag delta. Chromium and Firefox route the duplicate differently, producing cross-browser inconsistency. Use pointer capture alone: listen to `onPointerMove`/`onPointerUp` on the divider element itself.

Size update formula:
```
newSizes[i]   = max(10, startSizes[i] + delta)
newSizes[i+1] = max(10, startSizes[i+1] - delta)
```

`onPaneResize` fires with the updated `panes` array (each pane's `size` set to pixel value).

---

## 3. Initialization

Sizes are initialized in a `useEffect` triggered by `panes.length` or `orientation` changes. The container's `offsetWidth`/`offsetHeight` is read to calculate pixel values. A loading state exists briefly before sizes are computed (`if (!sizes.length) return <div ref={containerRef} />`).

---

## 4. No keyboard resize in M1

The divider handles pointer events only. Keyboard arrow-key resize is not implemented.

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-SP1 | High | No keyboard resize — dividers cannot be moved via keyboard | Accepted-risk M1 |
| G-SP2 | Medium | `SplitterPane.min` and `SplitterPane.max` accepted but not enforced | Accepted-risk M1; 10px hardcoded minimum only |
| G-SP3 | Low | No snap-to-collapse behavior when dragged to minimum | Accepted-risk M1 |
