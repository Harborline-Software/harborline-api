# Signature — Accessibility Contract

- **Component:** Signature
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Signature.Semantic.md) · [Interaction](./Signature.Interaction.md) · [Accessibility](./Signature.Accessibility.md) · [Styling](./Signature.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Signature.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

Signature pairs its pointer-only `<canvas>` with a native typed-name input.
The typed path is the keyboard and assistive-technology equivalent: it produces
the same SVG/PNG value through `onChange`, without requiring keyboard drawing
gestures. The canvas remains a pointer enhancement, not the only way to sign.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| `<canvas>` | `img` | Accessible name "Signature pad"; described by pointer/keyboard instructions; contains fallback text |
| Typed-name `<input>` | native `textbox` | Associated visible label "Your name (as signature)" |
| Clear `<button>` | `button` | `aria-label="Clear signature"` |

---

## 3. Canvas accessibility

The canvas has `role="img"`, an accessible name, descriptive fallback text, and
`aria-describedby` pointing to instructions that identify drawing as a pointer
path and the typed input as the keyboard path.

`showTypedInput` defaults to `true`. Setting it to `false` is conformant only
when the host provides an equivalent keyboard capture path and an AT-visible
instruction connecting the canvas to that path. `ESignatureField` is the
canonical example: its Type tab remains available and its draw instructions
identify that tab.

The typed input:

- is reachable in normal tab order;
- has a native `<label>`;
- uses standard text editing with no custom keyboard handling;
- is disabled when the component is disabled;
- clears freehand strokes and emits through the existing `onChange` contract.

This satisfies WCAG 2.2 SC 1.1.1 Non-text Content and SC 2.1.1 Keyboard for
standalone capture while preserving canvas drawing for pointer users.

---

## 4. Clear button

`aria-label="Clear signature"` gives the Clear button an accessible name. The
button and typed input both use the shared two-pixel `focus-visible` ring.

---

## 5. Disabled state

Disabled canvas has `cursor-not-allowed opacity-50` and `aria-disabled="true"`.
The typed input and Clear button have native `disabled` attributes.

---

## 6. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | Hosts can hide the built-in typed input | Low | `showTypedInput={false}` requires a documented host-owned equivalent; review consumers for that invariant |
| G2 | A typed signature is a rendered representation of a name, not handwriting | Accepted trade-off | The typed path is explicitly labelled and emits the same capture format |
