# CopyButton — Accessibility Contract

- **Component:** CopyButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CopyButton.Semantic.md) · [Interaction](./CopyButton.Interaction.md) · [Styling](./CopyButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/CopyButton.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA label

`aria-label` switches dynamically: `copied ? successLabel : label`. Screen readers announce the current state label when the button is focused or activated.

---

## 2. Live region

`aria-live="polite"` on the button — announces the label change to screen readers when `copied` transitions to `true`. The polite setting waits for the AT to finish its current announcement before announcing the state change.

---

## 3. Icons

All SVG icons have `aria-hidden` — accessible name comes from `aria-label`, not icon content.

---

## 4. Known gaps

None identified.
