# Accordion — Interaction Contract

- **Component:** Accordion
- **ADR 0017 family:** Layout
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Accordion.Semantic.md) · [Accessibility](./Accordion.Accessibility.md) · [Styling](./Accordion.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/layout/Accordion.tsx`
- **Catalog rows:** #155 Accordion / #35 PanelBar (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1 (R8 priority bump)

---

## 1. Toggle behavior

| Action | Condition | Effect |
|---|---|---|
| Click header button | `type='single'`, item open, `collapsible=true` | Close item |
| Click header button | `type='single'`, item open, `collapsible=false` | No-op (last open item cannot collapse) |
| Click header button | `type='single'`, item closed | Close any other open item, open this item |
| Click header button | `type='multiple'`, item open | Close item |
| Click header button | `type='multiple'`, item closed | Open item (others stay open) |
| Click header button | `item.disabled` | No-op (HTML `disabled` attribute) |

---

## 2. Keyboard navigation (header buttons)

| Key | Effect |
|---|---|
| `ArrowDown` | Focus next enabled header (wraps to first) |
| `ArrowUp` | Focus previous enabled header (wraps to last) |
| `Home` | Focus first enabled header |
| `End` | Focus last enabled header |
| `Enter` / `Space` | Toggle item (native button behavior) |

Navigation skips disabled items. Focus managed via `headerRefs` array.

---

## 3. Initial state

`defaultValue` seeds the initial open items:
- `type='single'`: only the first matching non-disabled item in `defaultValue` opens.
- `type='multiple'`: all matching non-disabled items open.

When `collapsible=false` and `type='single'` and `defaultValue` is empty, the first non-disabled item opens automatically.

---

## 4. Known gaps

None. The accordion interaction model is fully implemented per WAI-ARIA Accordion pattern.
