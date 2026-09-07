# Typography — Accessibility Contract

- **Component:** Typography
- **ADR 0017 family:** Typography
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Typography.Semantic.md) · [Interaction](./Typography.Interaction.md) · [Styling](./Typography.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Typography.tsx`
- **Catalog row:** #143 Typography (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Semantic HTML

Typography renders semantic HTML elements by default:

- `h1`–`h6` variants render heading elements → AT users can navigate by heading.
- `body1`/`body2` render `<p>` → paragraph semantics.
- `caption`/`overline`/`label` render `<span>` → inline text, no block semantics.

Using heading variants (`h1`–`h6`) creates heading landmarks. The host is responsible for maintaining correct heading hierarchy on the page.

---

## 2. `as` prop and semantic impact

Using `as` to change the rendered element can break semantics:

```tsx
// Correct: visual heading that is semantically a heading
<Typography variant="h2">Title</Typography>

// May break semantics: h3-styled text rendered as a div
<Typography variant="h3" as="div">Title</Typography>
```

Use `as` only when the visual style must differ from the structural element (e.g., `as="h2"` on a `body1` variant for a minor heading that uses body text styling).

---

## 3. Color contrast

`caption` and `overline` variants use `text-muted-foreground`. The host is responsible for verifying contrast (WCAG AA: 4.5:1 for `text-xs` content) against the page background. Muted foreground may fail contrast on light/non-white backgrounds.
