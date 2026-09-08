# AspectRatio — Semantic Contract

- **Component:** AspectRatio
- **ADR 0017 family:** Layout
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./AspectRatio.Interaction.md) · [Accessibility](./AspectRatio.Accessibility.md) · [Styling](./AspectRatio.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no implementation yet; source: Radix @radix-ui/react-aspect-ratio)
- **Catalog row:** #A15 AspectRatio (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix AspectRatio baseline)
- **Interaction-class:** display-only (no user interaction; render-only component)

---

## 1. Component purpose

**AspectRatio** — constrains content to a specific width-to-height ratio. Ensures consistent layout for images, videos, maps, and media embeds regardless of the container width. Uses a CSS padding-top trick or Radix's implementation.

---

## 2. Props (planned)

```typescript
interface AspectRatioProps extends React.HTMLAttributes<HTMLDivElement> {
  ratio?: number        // width/height; default: 1 (1:1 square)
  children?: React.ReactNode
}
```

Examples: `ratio={16/9}` → widescreen; `ratio={4/3}` → standard; `ratio={1}` → square.

---

## 3. Implementation strategy

Wraps content in a `position: relative` container. Inner content is `position: absolute; inset: 0`. The container's aspect ratio is enforced via the CSS `aspect-ratio` property or via a padding-top percentage fallback.

---

## 4. Children

Content (`children`) fills the AspectRatio container completely (absolute positioning). Callers pass images, videos, or other media as children with `object-fit: cover` where appropriate.
