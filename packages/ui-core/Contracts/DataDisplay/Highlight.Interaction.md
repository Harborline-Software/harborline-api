# Highlight — Interaction Contract

- **Component:** Highlight
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Highlight.Semantic.md) · [Interaction](./Highlight.Interaction.md) · [Accessibility](./Highlight.Accessibility.md) · [Styling](./Highlight.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Highlight.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

Both `Highlight` and `SearchHighlight` are **non-interactive presentational components**. They hold no state and emit no callbacks.

---

## 2. No interaction surface

`Highlight` and `SearchHighlight` do **not**:

- Fire `onClick` or any callback.
- Maintain internal state.
- Respond to keyboard input.
- Participate in the tab order.

---

## 3. `SearchHighlight` re-render behaviour

`SearchHighlight` re-computes its split output on every render when `text` or `query` changes. There is no memoisation in the reference implementation. For very large text strings or high-frequency updates (e.g., keystroke-by-keystroke search), hosts should memoize the component or the `text`/`query` values externally.

---

## 4. Empty query

When `query.trim()` is empty, `SearchHighlight` returns the full text in a plain `<span>` — no `<Highlight>` wrappers, no regex execution.

---

## 5. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | No memoization of regex split | Potential performance issue for large texts; host should memoize |
| I2 | No match count / no navigation between matches | For search-in-page patterns, host must track matches externally |
