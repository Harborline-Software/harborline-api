# ColumnVisibilityMenu — Styling Contract

- **Component:** ColumnVisibilityMenu
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ColumnVisibilityMenu.Semantic.md) · [Interaction](./ColumnVisibilityMenu.Interaction.md) · [Accessibility](./ColumnVisibilityMenu.Accessibility.md) · [Styling](./ColumnVisibilityMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/structural/ColumnVisibilityMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Trigger button

```
relative flex items-center gap-1.5 rounded-md border border-gray-300 bg-white
px-3 py-2 text-sm text-gray-700 hover:bg-gray-50
```

- Icon: `<Columns class="h-4 w-4" aria-hidden />`
- Label: `<span class="hidden sm:inline">Columns</span>` (hidden on mobile)

### Hidden-count badge

```
ml-1 rounded-full bg-blue-100 px-1.5 py-0.5 text-xs font-medium text-blue-700
```

Rendered only when `hiddenCount > 0`.

---

## 2. Popover content

```
z-10 min-w-[10rem] rounded-md border border-gray-200 bg-white py-1 shadow-md
```

Aligned to end of trigger via Radix `align="end"` with `sideOffset={4}`.

---

## 3. Column row

```
flex cursor-pointer items-center gap-2 px-3 py-1.5 text-sm text-gray-700 hover:bg-gray-50
```

### Checkbox (Radix Checkbox.Root)

```
flex h-3.5 w-3.5 shrink-0 items-center justify-center rounded border border-gray-300 bg-white
data-[state=checked]:border-blue-600 data-[state=checked]:bg-blue-600
disabled:cursor-not-allowed disabled:opacity-50
```

Checkbox indicator (checkmark):

```
<Check class="h-3 w-3 text-white" aria-hidden />
```

---

## 4. Visual states

| State | Element | Visual treatment |
|---|---|---|
| Trigger: default | Button | `bg-white border-gray-300` |
| Trigger: hover | Button | `hover:bg-gray-50` |
| Trigger: with hidden count | Badge span | `bg-blue-100 text-blue-700` |
| Popover: open | Content | `border-gray-200 bg-white shadow-md` |
| Row: default | Row div | `text-gray-700` |
| Row: hover | Row div | `hover:bg-gray-50` |
| Checkbox: unchecked | `.Root` | `border-gray-300 bg-white` |
| Checkbox: checked | `.Root` | `border-blue-600 bg-blue-600` |
| Checkbox: disabled | `.Root` | `cursor-not-allowed opacity-50` |

---

## 5. Dark mode

The reference implementation uses explicit light-mode colours (`bg-white`, `border-gray-200`, etc.). Dark mode is not implemented; provider themes must supply `dark:` overrides via the token system or `className` injection.

---

## 6. Responsive behaviour

- Trigger label `"Columns"` text: `hidden sm:inline` — visible on sm+ viewports; icon-only on mobile.
- Popover width: `min-w-[10rem]` (fixed minimum; grows with content).
- No layout changes at other breakpoints.
