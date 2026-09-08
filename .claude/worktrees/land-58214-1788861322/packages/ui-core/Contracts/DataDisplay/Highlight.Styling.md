# Highlight — Styling Contract

- **Component:** Highlight
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Highlight.Semantic.md) · [Interaction](./Highlight.Interaction.md) · [Accessibility](./Highlight.Accessibility.md) · [Styling](./Highlight.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/badges/Highlight.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Base recipe (`Highlight`)

```
rounded px-0.5 py-px font-medium
```

Applied to the `<mark>` element in addition to the colour-specific classes.

---

## 2. Colour recipes

| `color` | Tailwind classes |
|---|---|
| `yellow` | `bg-yellow-100 text-yellow-900` |
| `green` | `bg-green-100 text-green-900` |
| `blue` | `bg-blue-100 text-blue-900` |
| `orange` | `bg-orange-100 text-orange-900` |
| `pink` | `bg-pink-100 text-pink-900` |
| `purple` | `bg-purple-100 text-purple-900` |

The tinted palette (`{color}-100` bg, `{color}-900` text) was chosen to:
- Maintain high text contrast (≥10:1 in all cases — see Accessibility §5).
- Not overwhelm the surrounding text visually.
- Distinguish multiple highlight colours in the same context.

---

## 3. `SearchHighlight` output structure

`SearchHighlight` renders:

```html
<span class="{className}">
  plain text
  <mark class="rounded px-0.5 py-px font-medium bg-yellow-100 text-yellow-900">matched term</mark>
  plain text
  ...
</span>
```

The outer `<span>` receives the `className` prop. Matched segments receive the `<Highlight>` colour classes.

---

## 4. Visual states

`Highlight` is non-interactive. There is one visual state: the highlighted text.

| State | Treatment |
|---|---|
| Default | Tinted background + dark text |
| Hover / focus / active | None — non-interactive |

---

## 5. Dark mode

The reference implementation uses explicit `{color}-100` / `{color}-900` pairs. These are light-palette colours that do NOT adapt to dark mode.

**Dark mode recommendation:** Provider themes should supply `dark:` overrides (e.g., `dark:bg-yellow-900/40 dark:text-yellow-100`) for each colour variant. These are not in the reference implementation.

---

## 6. Token surface

No `--sf-*` tokens are defined for Highlight. Token migration path:

| Proposed token | Current value |
|---|---|
| `--sf-highlight-bg-{color}` | `{color}-100` |
| `--sf-highlight-fg-{color}` | `{color}-900` |
