# SortControl — Styling Contract

- **Component:** SortControl
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SortControl.Semantic.md) · [Interaction](./SortControl.Interaction.md) · [Accessibility](./SortControl.Accessibility.md) · [Styling](./SortControl.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/SortControl.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Group wrapper

```
flex items-center gap-1
```

---

## 2. Sort field select

```
rounded-md border border-gray-300 bg-white py-2 pl-2 pr-6 text-sm text-gray-700
focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500
```

Native `<select>` with browser-default appearance. `pr-6` leaves room for the browser's native dropdown arrow.

---

## 3. Direction toggle button

```
flex items-center rounded-md border border-gray-300 bg-white p-2 text-gray-500
hover:bg-gray-50 hover:text-gray-700
```

Icon: `<DirIcon class="h-4 w-4" aria-hidden />`

---

## 4. Icons

| `value` | Icon component | Meaning |
|---|---|---|
| `null` | `ArrowUpDown` | No sort direction set |
| `{direction: 'asc'}` | `ArrowUp` | Ascending |
| `{direction: 'desc'}` | `ArrowDown` | Descending |

---

## 5. Visual states

| Element | State | Treatment |
|---|---|---|
| Select | Default | `bg-white border-gray-300 text-gray-700` |
| Select | Focus | `focus:border-blue-500 focus:ring-blue-500` |
| Toggle button | Default | `bg-white border-gray-300 text-gray-500` |
| Toggle button | Hover | `hover:bg-gray-50 hover:text-gray-700` |

---

## 6. Dark mode

The reference implementation uses explicit light-mode colours. Dark mode not implemented. Provider themes supply `dark:` overrides.

---

## 7. Responsive behaviour

SortControl renders compactly — no responsive breakpoint changes. On mobile, the select label text may be truncated depending on host container width.
