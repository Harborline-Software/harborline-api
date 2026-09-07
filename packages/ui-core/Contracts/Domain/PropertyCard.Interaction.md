# PropertyCard — Interaction Contract

- **Component:** PropertyCard
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./PropertyCard.Semantic.md) · [Interaction](./PropertyCard.Interaction.md) · [Accessibility](./PropertyCard.Accessibility.md) · [Styling](./PropertyCard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/PropertyCard.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

PropertyCard's own interaction surface is **zero** — the card body is non-interactive. All interaction is in the `actions` slot.

---

## 2. No card-level interaction

PropertyCard does **not**:

- Fire any click event on the card body.
- Support hover highlight on the card itself.
- Support selection or multi-select.
- Navigate on card click.

If hosts need a clickable card, they should wrap PropertyCard in an `<a>` or a clickable container. PropertyCard's `className` prop allows custom styling of the clickable wrapper if it passes className down.

---

## 3. Actions slot interaction

The `actions` slot is a pass-through — all interactive behaviour comes from the host's content:

```tsx
<PropertyCard
  actions={
    <>
      <button onClick={() => navigate(`/properties/${id}`)}>View</button>
      <button onClick={handleEdit}>Edit</button>
    </>
  }
/>
```

---

## 4. Known gaps

| # | Gap | Notes |
|---|---|---|
| I1 | No card-level `onClick` prop | Host wraps in a link/button if card click is needed |
| I2 | No hover state on card body | Static card; no CSS hover treatment |
| I3 | No selected state | Host applies `className` override with a selection ring |
