# Icon — Interaction Contract

- **Component:** Icon
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Icon.Semantic.md) · [Interaction](./Icon.Interaction.md) · [Accessibility](./Icon.Accessibility.md) · [Styling](./Icon.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Icon.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

Both `Icon` and `SVGIcon` are **non-interactive**. There is no interaction surface.

---

## 2. No interaction surface

- No event callbacks.
- No internal state.
- No keyboard handling.
- Not focusable.

Icons are always composed inside interactive parents (buttons, links, nav items) when interaction is required. The interactive parent owns all interaction semantics.

---

## 3. Prop update behaviour

Icon re-renders synchronously on prop change. `name` changes swap the `data-icon` attribute; the CSS rule re-applies the new glyph.

---

## 4. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | No icon validation at runtime | An invalid `name` value produces a blank glyph with no error or warning |
| I2 | No click-to-copy or click-to-zoom built in | Host wraps in interactive element if needed |
