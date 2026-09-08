# GlobalSearch — Styling Contract

- **Component:** GlobalSearch
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./GlobalSearch.Semantic.md) · [Interaction](./GlobalSearch.Interaction.md) · [Accessibility](./GlobalSearch.Accessibility.md) · [Styling](./GlobalSearch.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/GlobalSearch.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root container

`relative` — positions the dropdown absolutely relative to the input wrapper.

## 2. Input wrapper

`relative` — positions the leading search icon.

## 3. Input element

```
w-full rounded-xl border border-gray-200 bg-white pl-9 pr-4 py-2
text-sm text-gray-900 placeholder-gray-400 shadow-sm
focus:outline-none focus-visible:ring-2 focus-visible:ring-blue-500 focus-visible:border-blue-300
```

- `pl-9` accommodates the leading icon at `left-3`.
- `rounded-xl` — extra-rounded pill style consistent with command-bar aesthetics.
- Focus ring: `ring-blue-500` / border `blue-300`.

## 4. Leading icon

```
absolute left-3 top-1/2 -translate-y-1/2 text-gray-400 text-sm
```

Decorative. `aria-hidden="true"`.

## 5. Dropdown panel

```
absolute left-0 right-0 top-full mt-1 z-50
rounded-xl border border-gray-200 bg-white shadow-lg py-1
max-h-80 overflow-y-auto
```

- `z-50` — floats above most page content.
- `max-h-80` — scrollable at tall result sets.
- `rounded-xl` — matches the input.

## 6. Category heading

```
px-3 pt-2 pb-1 text-xs font-semibold text-gray-400 uppercase tracking-wide
```

## 7. Result item (button)

Default: `w-full flex items-center gap-3 px-3 py-2 text-left transition-colors focus:outline-none hover:bg-gray-50`

Active (keyboard): `bg-blue-50`

## 8. Result icon

```
text-gray-400 w-5 h-5 flex items-center justify-center shrink-0 text-sm
```

## 9. Result label

Primary: `text-sm text-gray-900 truncate`

Sublabel: `text-xs text-gray-400 truncate`

## 10. CSS variables

The component uses only Tailwind utility classes with no CSS custom properties. Theming is via Tailwind configuration.
