# ListToolbar — Styling Contract

- **Component:** ListToolbar
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ListToolbar.Semantic.md) · [Interaction](./ListToolbar.Interaction.md) · [Accessibility](./ListToolbar.Accessibility.md) · [Styling](./ListToolbar.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/ListToolbar.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

```
space-y-2
```

---

## 2. Toolbar row

```
flex flex-wrap items-center gap-3 rounded-lg border border-gray-200 bg-gray-50 p-3
```

---

## 3. Search slot container

```
min-w-[12rem] flex-1
```

The search slot grows to fill remaining space after filters and actions have their natural width.

---

## 4. Filters slot container

```
flex flex-wrap items-center gap-2
```

---

## 5. Actions slot container

```
ml-auto flex items-center gap-2
```

`ml-auto` pushes actions to the right edge.

---

## 6. "Clear filters" row

```
flex items-center
```

"Clear filters" button:

```
text-xs text-blue-600 hover:text-blue-800 hover:underline
```

---

## 7. Visual states

| Element | State | Treatment |
|---|---|---|
| Toolbar row | Default | `bg-gray-50 border-gray-200` |
| "Clear filters" button | Default | `text-blue-600` |
| "Clear filters" button | Hover | `text-blue-800 underline` |

---

## 8. Dark mode

The reference implementation uses explicit light-mode colours (`bg-gray-50`, `border-gray-200`). Dark mode not implemented. Provider themes supply `dark:` overrides.

---

## 9. Responsive behaviour

The toolbar row uses `flex-wrap` — on narrow viewports, the filters and actions wrap below the search input. The search input has `min-w-[12rem]` to prevent it from collapsing too small.

No explicit breakpoint classes other than those delegated to slot components (e.g., `hidden sm:inline` on child labels).

---

## 10. Tailwind v4 constraint

**CRITICAL:** All Tailwind classes in ListToolbar are literal strings. Do NOT construct class names dynamically (e.g., template literal interpolation). Tailwind v4 content-detection only emits CSS for literal class substrings. This constraint applies to any future extension of the component.
