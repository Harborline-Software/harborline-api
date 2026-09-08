# FilterChips — Styling Contract

- **Component:** FilterChips
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FilterChips.Semantic.md) · [Interaction](./FilterChips.Interaction.md) · [Accessibility](./FilterChips.Accessibility.md) · [Styling](./FilterChips.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FilterChips.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

```
flex flex-wrap gap-2
```

---

## 2. Chip button — base

```
inline-flex items-center gap-1.5 rounded-full px-3 py-1 text-sm font-medium transition-colors
focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-blue-500
```

### 2.1 Selected state

```
bg-blue-600 text-white
```

### 2.2 Unselected state

```
bg-gray-100 text-gray-700 hover:bg-gray-200
```

---

## 3. Count badge

### Selected chip count

```
rounded-full px-1.5 py-px text-xs font-semibold bg-blue-500 text-white
```

### Unselected chip count

```
rounded-full px-1.5 py-px text-xs font-semibold bg-gray-200 text-gray-600
```

---

## 4. Visual states

| State | Chip | Count |
|---|---|---|
| Unselected, default | `bg-gray-100 text-gray-700` | `bg-gray-200 text-gray-600` |
| Unselected, hover | `bg-gray-200 text-gray-700` | unchanged |
| Selected | `bg-blue-600 text-white` | `bg-blue-500 text-white` |
| Focus-visible | `ring-2 ring-blue-500` (both states) | — |

---

## 5. Dark mode

The reference implementation uses explicit light-mode colours. Dark mode not implemented. Provider themes supply `dark:` overrides.

---

## 6. Responsive behaviour

The container uses `flex-wrap` — chips reflow to additional rows on narrow viewports. No explicit breakpoint changes.
