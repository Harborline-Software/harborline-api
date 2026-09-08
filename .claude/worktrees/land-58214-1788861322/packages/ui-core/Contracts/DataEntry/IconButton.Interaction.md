# IconButton — Interaction Contract

- **Component:** IconButton
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./IconButton.Semantic.md) · [Accessibility](./IconButton.Accessibility.md) · [Styling](./IconButton.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/IconButton.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Click behavior

Native `<button>` click. `onClick` fires unless `disabled=true` or `loading=true`.

`type="button"` is always set — does not submit forms.

---

## 2. Disabled state

`disabled={disabled || loading}` — button is disabled during loading regardless of the `disabled` prop.

---

## 3. Loading state

While `loading=true`: button is disabled, shows spinner, `aria-busy=true`. No click events fire.

---

## 4. Keyboard behavior

Native button keyboard behavior — Enter and Space activate the button.

---

## 5. Known gaps

None identified.
