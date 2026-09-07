# UserMenu — Styling Contract

- **Component:** UserMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted — PAO amendment required (see note below)
- **Companion contracts:** [Semantic](./UserMenu.Semantic.md) · [Interaction](./UserMenu.Interaction.md) · [Accessibility](./UserMenu.Accessibility.md) · [Styling](./UserMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/UserMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation); PAO amendment pending (ADR 0121 Phase 1)

---

> **PAO to amend — see ADR 0121 Appendix A for required Styling contract additions:**
> - `placement="top"` dropdown positioning styles (`bottom-full mb-2` vs `top-full mt-2`)
> - `align` variants: `right-0` (start/default), `left-0` (end), `left-1/2 -translate-x-1/2` (center)
> - `aria-hidden` avatar requirement per ADR 0121 §D8 (avatar circle should carry `aria-hidden="true"` when used as a decorative element alongside a visible name)
> - `subtitle` text style in menu items (secondary text below label)
> - `render` custom slot wrapper style (the `<div role="menuitem">` container's padding/chrome)
> - Theme `menuitemradio` pattern if a radio-style theme control ships as a custom item

---

## 1. Root container

`relative inline-block`

## 2. Trigger button

```
flex items-center gap-2 rounded-full
focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2
```

## 3. Avatar circle

```
w-8 h-8 rounded-full bg-blue-600 text-white
flex items-center justify-center text-sm font-semibold overflow-hidden shrink-0
```

When avatar image is present: `<img>` fills the circle via `w-full h-full object-cover`.

## 4. Name + role text (trigger, md+ only)

Container: `hidden md:flex flex-col items-start`

Name: `text-sm font-medium text-gray-900 leading-tight`

Role: `text-xs text-gray-500 leading-tight`

## 5. Chevron (md+ only)

`hidden md:block w-4 h-4 text-gray-400 transition-transform`

When open: `rotate-180`

## 6. Dropdown panel

```
absolute right-0 mt-2 w-56 bg-white border border-gray-200 rounded-lg shadow-lg z-50 py-1
```

## 7. Info header (in dropdown)

Container: `px-4 py-3 border-b border-gray-100`

Name: `text-sm font-medium text-gray-900`

Email: `text-xs text-gray-500 truncate mt-0.5`

Role: `text-xs text-blue-600 mt-0.5`

## 8. Menu item (link or button)

Default: `flex items-center gap-2 px-4 py-2 text-sm hover:bg-gray-50 transition-colors text-gray-700`

Danger: `text-red-600 hover:bg-red-50`

Button items additionally: `w-full text-left`

## 9. Divider

`my-1 border-t border-gray-100`
