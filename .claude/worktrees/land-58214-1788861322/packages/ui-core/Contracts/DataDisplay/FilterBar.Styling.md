# FilterBar — Styling Contract

- **Component:** FilterBar
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FilterBar.Semantic.md) · [Interaction](./FilterBar.Interaction.md) · [Accessibility](./FilterBar.Accessibility.md) · [Styling](./FilterBar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/FilterBar.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Active filters container

```
flex flex-wrap items-center gap-2
```

---

## 2. Individual chip

```
inline-flex items-center gap-1.5 rounded-full bg-blue-50 border border-blue-200
px-3 py-1 text-sm text-blue-700
```

- Label span: `font-medium`
- Value span: `text-blue-500` (slightly lighter than label)

---

## 3. Remove button

```
ml-0.5 rounded-full p-0.5 text-blue-400 hover:bg-blue-200 hover:text-blue-700
focus:outline-none transition-colors
```

**Known gap:** `text-blue-400` fails WCAG 2.2 SC 1.4.11 non-text contrast (see Accessibility §6.A2). Update to `text-blue-600`.

---

## 4. "Clear all" button

```
text-xs text-gray-500 hover:text-gray-700 hover:underline focus:outline-none
```

---

## 5. Empty state

```
flex items-center gap-2 text-sm text-gray-400
```

- Icon: `text-base` inline span (decorative `⊘`).

---

## 6. Visual states

| Element | State | Treatment |
|---|---|---|
| Chip | Default | `bg-blue-50 border-blue-200 text-blue-700` |
| Remove button | Default | `text-blue-400` |
| Remove button | Hover | `bg-blue-200 text-blue-700` |
| "Clear all" | Default | `text-gray-500` |
| "Clear all" | Hover | `text-gray-700 underline` |

---

## 7. Dark mode

The reference implementation uses explicit light-mode Tailwind colour utilities. Dark mode is not implemented. Provider themes supply `dark:` overrides.

---

## 8. Responsive behaviour

The chip container uses `flex-wrap` — chips reflow to additional rows on narrow viewports. No explicit breakpoint behaviour.
