# ValidationTooltip — Interaction Contract

- **Component:** ValidationTooltip
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ValidationTooltip.Semantic.md) · [Accessibility](./ValidationTooltip.Accessibility.md) · [Styling](./ValidationTooltip.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #147 ValidationTooltip (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Telerik ValidationTooltip baseline)

---

## 1. Show / hide

Auto mode (`show` omitted): shown when `messages` is non-empty. Callers control validation state; this component just displays it.

Controlled mode (`show` provided): shown when `show=true` regardless of `messages` content.

---

## 2. Position update

When the associated input scrolls or the page resizes, the tooltip should reposition itself. (Forward-spec — exact strategy TBD in M2.)

---

## 3. No user interaction

The tooltip is display-only. It has no dismiss button, no hover interaction, and no click behavior.

---

## 4. Known gaps

None identified for forward-spec. Validate against actual implementation in M2.
