# UserMenu — Semantic Contract

- **Component:** UserMenu
- **ADR 0017 family:** Navigation
- **Contract type:** Semantic
- **Status:** Accepted — amended per ADR 0121 Appendix A (2026-06-15)
- **Companion contracts:** [Interaction](./UserMenu.Interaction.md) · [Accessibility](./UserMenu.Accessibility.md) · [Styling](./UserMenu.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/UserMenu.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation); amended ADR 0121 Phase 1
- **Foundation:** none — hand-rolled dropdown menu

---

## 1. Component purpose

**UserMenu** — an avatar trigger button that opens a dropdown panel showing user identity (name, email, role) and a list of user-specific actions. Typically placed at the right end of an AppBar or the footer of a sidebar (`placement="top"`). Handles avatar images and initials fallback. Supports inline custom items (e.g. a theme toggle) via the `render` prop on `UserMenuItem`.

---

## 2. Data model

### 2a. UserMenuItem

```typescript
interface UserMenuItem {
  id: string
  label: string
  icon?: React.ReactNode    // was string (emoji/text) in M1; now ReactNode per ADR 0121
  subtitle?: string         // NEW — secondary text displayed below the label
  onSelect?: () => void     // preferred handler; replaces onClick
  /** @deprecated Use onSelect instead */
  onClick?: () => void      // backward-compat alias; resolved as (onSelect ?? onClick)
  closeOnSelect?: boolean   // default true; false keeps menu open after interaction
  kind?: 'action' | 'link' | 'custom'  // discriminant for rendering intent
  render?: React.ReactNode  // when present, renders instead of label+icon (custom slot)
  // Legacy / backward-compat props
  href?: string             // keep for link kind; triggers <a> rendering
  danger?: boolean          // intent token — maps to destructive styling
  divider?: boolean         // keep for separator items
}
```

**Intent tokens:** use `danger` (not `error`) for any destructive item, per the pinned family-wide vocabulary in ADR 0121.

**Handler resolution:** internal code calls `item.onSelect ?? item.onClick` so existing call sites using `onClick` continue to work without modification.

### 2b. UserMenuHeader

```typescript
interface UserMenuHeader {
  name: string
  email?: string
  role?: string
  avatar?: string   // URL of avatar image
}
```

### 2c. UserMenuProps

```typescript
interface UserMenuProps {
  // NEW structured header per ADR 0121 Appendix A
  header?: UserMenuHeader
  // Flat props retained for backward compat with existing tests + call sites
  /** @deprecated Use header.name instead */
  name?: string
  /** @deprecated Use header.email instead */
  email?: string
  /** @deprecated Use header.avatar instead */
  avatar?: string
  /** @deprecated Use header.role instead */
  role?: string
  items?: UserMenuItem[]      // default: []
  onSignOut?: () => void      // when undefined, sign-out row is NOT rendered
  placement?: 'bottom' | 'top'  // default 'bottom'; 'top' positions dropdown above trigger
  align?: 'start' | 'center' | 'end'  // default 'start'
  className?: string
}
```

**Identity resolution:** `header` takes precedence over flat props. If both are provided, `header.name` wins over `name`, `header.email` wins over `email`, etc. If only flat props are provided, they serve as the identity source (backward compat).

---

## 3. Avatar rendering

When `avatar` (or `header.avatar`) is provided: renders `<img src={avatar} alt={resolvedName} />` filling the circle.

When `avatar` is not provided: renders initials derived from `resolvedName`:
- Two or more words: first character of first word + first character of last word, uppercased.
- Single word: first two characters, uppercased.

---

## 4. Items assembly

The component assembles `allItems` from `items` + an auto-appended sign-out entry:

1. `items` props (as-provided).
2. If `items.length > 0` AND `onSignOut` is provided, a divider (`divider: true`) is appended.
3. If `onSignOut` is provided, a `{ id: '__signout__', label: 'Sign out', onSelect: onSignOut, danger: false }` item is appended.

The sign-out row is **only rendered when `onSignOut` is defined** — undefined = absent.

---

## 5. Divider items

Items with `divider: true` render as `<div role="separator">` with a horizontal rule. Divider items have no label.

---

## 6. Item rendering

| Item configuration | Renders as |
|---|---|
| `render` present | `<div role="menuitem">` containing `render` (custom slot; no label or icon) |
| `href` present (no `render`) | `<a role="menuitem">` |
| Neither (button variant) | `<button role="menuitem">` |

Items with `danger: true` receive destructive text colour and hover background (intent token: `danger`).

Items with `subtitle` render the subtitle text below the label in a secondary text style.

**Close-on-select behaviour:** after interaction, the menu closes unless `closeOnSelect: false` is set OR the item has `kind: 'custom'` with a `render` node present. This seam enables inline controls (e.g. theme toggle) that remain usable without reopening the menu.

---

## 7. Info header

The dropdown always begins with a non-interactive identity block showing `resolvedName`, `resolvedEmail` (optional), and `resolvedRole` (optional). This is visual display only; it is not a menu item.

---

## 8. Placement and alignment

| `placement` | Dropdown position |
|---|---|
| `'bottom'` (default) | Below the trigger (`top-full mt-2`) |
| `'top'` | Above the trigger (`bottom-full mb-2`) — for sidebar footer use |

| `align` | Dropdown alignment |
|---|---|
| `'start'` (default) | Aligned to the right edge of trigger (`right-0`) |
| `'end'` | Aligned to the left edge of trigger (`left-0`) |
| `'center'` | Centered below trigger (`left-1/2 -translate-x-1/2`) |

---

## 9. Related components

- **AppBar** — canonical host surface for UserMenu (`placement="bottom"`, default).
- **SideNav / sidebar footer** — use `placement="top"` to open upward.
- **ActionMenu** — a generic dropdown menu for action lists; UserMenu is specialised for user identity.
