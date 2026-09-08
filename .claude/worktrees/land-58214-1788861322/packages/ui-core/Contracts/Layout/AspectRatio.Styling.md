# AspectRatio — Styling Contract

- **Component:** AspectRatio
- **ADR 0017 family:** Layout
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AspectRatio.Semantic.md) · [Interaction](./AspectRatio.Interaction.md) · [Accessibility](./AspectRatio.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A15 AspectRatio (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix AspectRatio baseline)

---

## 1. Container

`position: relative; width: 100%`

Aspect ratio enforced via CSS `aspect-ratio: {ratio}` property.

---

## 2. Inner content wrapper

`position: absolute; inset: 0` — children fill the container.

---

## 3. Overflow

`overflow: hidden` on the container clips children to the constrained dimensions.

---

## 4. className passthrough

Applied to the outer container div.
