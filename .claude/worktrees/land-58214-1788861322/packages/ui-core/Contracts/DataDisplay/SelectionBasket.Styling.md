# SelectionBasket — Styling Contract

- **Component:** SelectionBasket
- **ADR 0017 family:** DataDisplay
- **Contract type:** Styling (tokens, theming, layout)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./SelectionBasket.Semantic.md) · [Interaction](./SelectionBasket.Interaction.md) · [Accessibility](./SelectionBasket.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/datagrid/SelectionBasket.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Token surface

Semantic tokens only — no raw colours, no hex, no palette literals.

| Surface | Token |
|---|---|
| Tray background | `bg-card` |
| Tray border | `border-border` |
| Title, item label | `text-foreground` |
| Description, group heading, clear, count badge | `text-muted-foreground` |
| Count badge fill, item hover, clear/remove hover | `bg-muted` (`/60`, `/80` for hover tiers) |
| Focus ring | `ring-primary` via `focus-visible:ring-2` |

Light/dark parity comes free: every token above is theme-resolved, so the `Dark` story needs no
component-side branch. There is no `dark:` variant anywhere in the implementation — a `dark:`
override would be the signal that a raw value had leaked in.

---

## 2. Layout

- Root: vertical flex, `gap-2`, `rounded-lg`, `p-3`.
- Header: horizontal flex, `justify-between` — title + badge on the leading edge, clear on the
  trailing edge.
- Groups: vertical flex, `gap-3` between groups, `gap-1` between a heading and its list.
- Item: `justify-between` with the label block leading and remove trailing; the label block is
  `min-w-0` + `truncate` so a long label ellipsizes instead of pushing the remove button out of
  the tray.

**Logical properties only.** Flex `justify-between` and `gap` are direction-agnostic, so the
tray mirrors correctly in RTL with no extra rules. No `ml-`/`pl-`/`left`/`text-left`.

---

## 3. Sizing

The basket has **no intrinsic width** — it fills its container. Callers set width via
`className` (the stories use `max-w-sm` / `w-64`). This keeps it usable both as a sidebar rail
and as a panel inside a wider layout without a size prop.

Height is unbounded: the basket does not scroll internally. A host expecting very long baskets
should constrain and scroll it from outside, so the scroll container is the one the host's
layout already manages.

---

## 4. Density

One density. The tray is a compact secondary surface by default (`text-sm` items, `text-xs`
descriptions and group headings); it does not participate in a comfortable/compact switch.

---

## 5. Do not

- Do not add a `variant` prop for domain flavours — see the Semantic contract §1.2.
- Do not colour items by state (overdue, error, …). Status colour belongs to the item's own
  presentation in the host surface; a tray that recolours by domain state is domain-aware.
- Do not add `dark:` overrides. If dark mode looks wrong, a raw value leaked into §1.
