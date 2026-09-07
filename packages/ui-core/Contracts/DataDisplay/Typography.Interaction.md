# Typography — Interaction Contract

- **Component:** Typography
- **ADR 0017 family:** DataDisplay
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Typography.Semantic.md) · [Interaction](./Typography.Interaction.md) · [Accessibility](./Typography.Accessibility.md) · [Styling](./Typography.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Typography.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Typography is non-interactive

Typography is a presentational text rendering component. It has no built-in click handlers, hover effects, focus management, or event callbacks.

All interactivity (links, click handlers, focus management) is the host's responsibility via the `as` prop or by composing Typography inside an interactive element.

---

## 2. Interactive usage patterns

Hosts that need interactive typography should:

- Wrap `<Typography>` inside an `<a>` or `<button>`.
- Pass `as="a"` with an `href` via passthrough attributes (Typography spreads nothing onto the element beyond `className` and `children` — no HTML attribute passthrough).
- Use `<Typography variant="label" as="label" htmlFor="...">` to render an accessible form label.

---

## 3. Known gaps

| Gap | Description |
|---|---|
| No HTML attribute passthrough | Unlike Badge (which spreads HTML attributes onto the root element), Typography does not spread arbitrary attributes. `id`, `data-*`, `aria-*`, `htmlFor`, `onClick` etc. cannot be passed through. Hosts must use `as` or wrap externally. |
| `Label` alias missing | The convenience `Label` export is absent (likely due to naming conflict risk). Hosts must use `<Typography variant="label">` directly. |
