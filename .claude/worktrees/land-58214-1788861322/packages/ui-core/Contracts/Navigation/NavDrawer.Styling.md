# NavDrawer — Styling Contract

- **Component:** NavDrawer
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NavDrawer.Semantic.md) · [Interaction](./NavDrawer.Interaction.md) · [Accessibility](./NavDrawer.Accessibility.md) · [Styling](./NavDrawer.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/NavDrawer.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Backdrop

```
fixed inset-0 z-40 bg-black/40 backdrop-blur-sm
```

`z-40` — below the drawer panel (`z-50`). Only rendered when `open` is `true`.

## 2. Drawer panel

```
fixed top-0 bottom-0 z-50 flex w-72 flex-col bg-white shadow-xl
transition-transform duration-300 ease-in-out focus:outline-none
```

Width is fixed at `w-72` (288px).

### Open/closed translation

| `side` | Closed | Open |
|---|---|---|
| `left` | `-translate-x-full` | `translate-x-0` |
| `right` | `translate-x-full` | `translate-x-0` |

## 3. Header row

`flex items-center justify-between px-4 py-3 border-b border-gray-100`

Default header label: `text-sm font-semibold text-gray-700`

## 4. Close button

```
rounded-md p-1.5 text-gray-400 hover:bg-gray-100 hover:text-gray-600
focus:outline-none focus-visible:ring-2 focus-visible:ring-gray-500 transition-colors
```

## 5. Nav content area

`flex-1 overflow-y-auto py-2` — scrollable; fills available space between header and footer.

## 6. Section heading

`px-4 py-1 text-xs font-semibold uppercase tracking-wider text-gray-400`

Sections after the first: `mt-4` top margin.

## 7. Nav item (default)

```
flex w-full items-center gap-3 px-4 py-2.5 text-sm transition-colors
text-gray-700 hover:bg-gray-50 hover:text-gray-900
```

## 8. Nav item (active)

`bg-blue-50 text-blue-700 font-medium`

## 9. Item icon

`h-5 w-5 shrink-0 flex items-center justify-center` with `aria-hidden="true"`

## 10. Badge chip

`rounded-full bg-blue-100 px-2 py-0.5 text-xs font-medium text-blue-700`

## 11. Footer

`border-t border-gray-100 px-4 py-3`
