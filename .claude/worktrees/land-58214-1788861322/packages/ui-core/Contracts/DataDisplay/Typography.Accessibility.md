# Typography — Accessibility Contract

- **Component:** Typography
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Typography.Semantic.md) · [Interaction](./Typography.Interaction.md) · [Accessibility](./Typography.Accessibility.md) · [Styling](./Typography.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Typography.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Semantic HTML output

Typography's accessibility story is its primary value: by mapping `variant` to the correct default HTML element, it ensures correct document semantics.

| Variant | Rendered element | Semantic benefit |
|---|---|---|
| `h1`–`h6` | `<h1>`–`<h6>` | Correct heading hierarchy; screen readers navigate by heading level |
| `body1`, `body2` | `<p>` | Paragraph semantics; screen readers distinguish paragraphs from inline text |
| `caption`, `overline`, `label` | `<span>` | Inline; no implicit landmark or heading semantics |

---

## 2. `as` prop and accessibility

The `as` prop can improve or degrade accessibility depending on use:

- `<Typography variant="body1" as="div">` — renders a block element but loses `<p>` semantics. Use only when `<p>` cannot be used (e.g. as a flex container).
- `<Typography variant="label" as="label" htmlFor="...">` — NOTE: `htmlFor` cannot be passed without HTML attribute passthrough (which is absent). Hosts must use a raw `<label>` wrapping Typography instead.
- `<Typography variant="h3" as="h2">` — adjusts heading level for document outline; the visual variant and the semantic level diverge intentionally.

---

## 3. Heading hierarchy

Typography renders the correct heading element for the variant by default. However, it cannot enforce document-level heading hierarchy — hosts are responsible for choosing the correct heading level for their page outline. Using `variant="h1"` on every section breaks the screen-reader heading tree.

---

## 4. Known gaps

| Gap | Severity | Description |
|---|---|---|
| No attribute passthrough | Medium | Typography does not pass `aria-*`, `id`, `htmlFor`, or other HTML attributes to the rendered element. Hosts cannot annotate Typography text with `aria-label`, `aria-describedby`, or `id` without wrapping. |
| `overline` / `caption` use `<span>` | Low | `overline` and `caption` render as `<span>` (inline). If the host uses these as block-level section labels, `<div>` or `<p>` may be more appropriate semantically. Use `as` to override. |
| No `role` override | Low | Typography cannot render with a custom ARIA role (e.g. `role="heading" aria-level="2"` for a non-standard heading element) without attribute passthrough. |
