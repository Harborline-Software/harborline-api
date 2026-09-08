# IconButton — Accessibility Contract

- **Component:** IconButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./IconButton.Semantic.md) · [Interaction](./IconButton.Interaction.md) · [Styling](./IconButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/IconButton.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Required `aria-label`

`aria-label` is required in the TypeScript interface. Icon-only buttons have no visible label text; `aria-label` is the sole accessible name. Screen readers announce it for every interaction.

---

## 2. Loading state

`aria-busy={loading || undefined}` — set to `true` during loading so AT announces the busy state.

Spinner SVG is `aria-hidden` — not announced to screen readers.

---

## 3. Disabled state

Uses native `disabled` attribute. Screen readers announce disabled state natively. No `aria-disabled` used.

---

## 4. Icon children

Callers should render icons as `aria-hidden` SVGs inside IconButton. The `aria-label` on the button provides the accessible name — the icon itself should not be announced.

---

## 5. Known gaps

None identified.
