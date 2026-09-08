# SavedViewsMenu — Styling Contract

- **Component:** SavedViewsMenu
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SavedViewsMenu.Semantic.md) · [Interaction](./SavedViewsMenu.Interaction.md) · [Accessibility](./SavedViewsMenu.Accessibility.md) · [Styling](./SavedViewsMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/SavedViewsMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Trigger button

Default (no saved views):

```
flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-3 py-2 text-sm text-gray-700 hover:bg-gray-50
```

With saved views (same classes; icon changes to `BookmarkCheck text-blue-600`).

View count:

```
ml-0.5 text-xs text-gray-500
```

Chevron:

```
h-3 w-3 text-gray-400
```

---

## 2. Popover content

```
z-10 w-56 rounded-md border border-gray-200 bg-white py-2 shadow-md
```

Aligned to end of trigger via Radix `align="end" sideOffset={4}`.

---

## 3. Save section

Section label:

```
mb-1 text-xs font-medium uppercase tracking-wide text-gray-400
```

Input:

```
min-w-0 flex-1 rounded border border-gray-300 px-2 py-1 text-xs
focus:border-blue-500 focus:outline-none
```

Save button:

```
rounded bg-blue-600 px-2 py-1 text-xs text-white disabled:opacity-40 hover:bg-blue-700
```

---

## 4. Saved views list

Section divider:

```
border-t border-gray-100 pt-1
```

Section label:

```
px-3 py-1 text-xs font-medium uppercase tracking-wide text-gray-400
```

View row:

```
flex items-center justify-between px-3 py-1.5 hover:bg-gray-50
```

Apply button (view name):

```
min-w-0 flex-1 truncate text-left text-sm text-gray-700 hover:text-gray-900
```

Delete button:

```
ml-2 rounded p-0.5 text-gray-400 hover:text-red-500
```

Delete icon: `<Trash2 class="h-3.5 w-3.5" aria-hidden />`

---

## 5. Empty state

```
px-3 py-1 text-xs text-gray-400
```

---

## 6. Visual states

| Element | State | Treatment |
|---|---|---|
| Trigger | Default | `bg-white border-gray-300` |
| Trigger | Hover | `hover:bg-gray-50` |
| Input | Focus | `focus:border-blue-500` |
| Save button | Active | `bg-blue-600` |
| Save button | Disabled | `opacity-40` |
| Save button | Hover | `hover:bg-blue-700` |
| View row | Hover | `hover:bg-gray-50` |
| Delete button | Default | `text-gray-400` |
| Delete button | Hover | `text-red-500` |

---

## 7. Dark mode

The reference implementation uses explicit light-mode colours. Dark mode not implemented. Provider themes supply `dark:` overrides.

---

## 8. Responsive behaviour

Popover width: `w-56` (fixed). No responsive behaviour — the popover is a fixed-width dropdown.

Trigger label `"Views"` text: `hidden sm:inline` — visible on sm+ viewports; icon-only on mobile.
