# HoverCard — Accessibility Contract

- **Component:** HoverCard
- **ADR 0017 family:** Overlays
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./HoverCard.Semantic.md) · [Interaction](./HoverCard.Interaction.md) · [Styling](./HoverCard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet)
- **Catalog row:** #A2 HoverCard (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix @radix-ui/react-hover-card baseline)

---

## 1. ARIA roles

No ARIA role on the card content — content is supplemental and non-interactive. Screen readers can read the card content when it's visible in the DOM.

Trigger: no special ARIA role. Callers may add `aria-describedby` pointing to a hidden text description if the HoverCard content is critical to understanding the trigger.

---

## 2. Keyboard accessibility limitation

HoverCard does not open via keyboard focus — this is intentional per Radix design. Content inside HoverCard must NOT be the only way to access critical information (WCAG 2.1 SC 1.3.1). For keyboard-accessible supplemental info, use Popover.

---

## 3. Focus containment

HoverCard does not trap focus. Users can Tab through the card content if it contains focusable elements.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-HC-A1 | High | Not keyboard-accessible — callers must not place critical information here | Accepted-risk; use Popover for keyboard-required cases |
