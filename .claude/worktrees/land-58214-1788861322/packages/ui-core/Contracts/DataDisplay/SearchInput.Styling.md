# SearchInput — Styling Contract

- **Component:** SearchInput
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SearchInput.Semantic.md) · [Interaction](./SearchInput.Interaction.md) · [Accessibility](./SearchInput.Accessibility.md) · [Styling](./SearchInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/SearchInput.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer wrapper

```
relative flex items-center
```

Plus `{className}` from prop.

---

## 2. Search icon

```
pointer-events-none absolute left-3 h-4 w-4 text-gray-400
```

Positioned absolutely at the left edge of the input.

---

## 3. Input

```
w-full rounded-md border border-gray-300 bg-white py-2 pl-9 pr-8 text-sm text-gray-900
placeholder-gray-400
focus:border-blue-500 focus:outline-none focus:ring-1 focus:ring-blue-500
```

- `pl-9` leaves room for the search icon.
- `pr-8` leaves room for the clear button.

---

## 4. Clear button

```
absolute right-2 rounded p-0.5 text-gray-400 hover:text-gray-600
```

Positioned absolutely at the right edge.

---

## 5. Visual states

| Element | State | Treatment |
|---|---|---|
| Input | Default | `border-gray-300 bg-white text-gray-900` |
| Input | Focus | `focus:border-blue-500 focus:ring-1 focus:ring-blue-500` |
| Search icon | Default | `text-gray-400` |
| Clear button | Default | `text-gray-400` |
| Clear button | Hover | `hover:text-gray-600` |

---

## 6. Dark mode

The reference implementation uses explicit light-mode colours (`bg-white`, `border-gray-300`, `text-gray-900`). Dark mode not implemented. Provider themes supply `dark:` overrides.

---

## 7. Responsive behaviour

The input is `w-full` — it fills its container. The container is `min-w-[12rem] flex-1` in ListToolbar, ensuring a minimum usable width.

---

## 8. Token surface

No `--sf-search-*` tokens are defined. Token migration path:

| Proposed token | Current value |
|---|---|
| `--sf-input-bg` | `bg-white` |
| `--sf-input-border` | `border-gray-300` |
| `--sf-input-border-focus` | `border-blue-500` |
| `--sf-input-text` | `text-gray-900` |
| `--sf-input-placeholder` | `placeholder-gray-400` |
