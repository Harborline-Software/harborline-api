# Label — Styling Contract

- **Component:** Label
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Label.Semantic.md) · [Interaction](./Label.Interaction.md) · [Accessibility](./Label.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no direct implementation)
- **Catalog row:** #74 Label (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix Label baseline)

---

## 1. Base

`text-sm font-medium leading-none peer-disabled:cursor-not-allowed peer-disabled:opacity-70`

`className` passthrough.

---

## 2. Typography

`text-sm` (14px), `font-medium`, `leading-none` — tight single-line label. Text color inherits from parent context.

---

## 3. Peer disabled state

`peer-disabled:cursor-not-allowed peer-disabled:opacity-70` — dims when the associated input (peer) is disabled.
