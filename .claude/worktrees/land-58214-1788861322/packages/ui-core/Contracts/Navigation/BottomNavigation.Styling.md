# BottomNavigation — Styling Contract

- **Component:** BottomNavigation
- **ADR 0017 family:** Navigation
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./BottomNavigation.Semantic.md) · [Interaction](./BottomNavigation.Interaction.md) · [Accessibility](./BottomNavigation.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/navigation/BottomNavigation.tsx`
- **Catalog row:** #12 BottomNavigation (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container (`<nav>`)

`fixed bottom-0 left-0 right-0 z-40`
`flex items-center justify-around h-16 px-2 pb-safe-bottom`
`border-t border-border`
Shadow (when `shadow=true`): `shadow-lg shadow-black/10`

Fill mode:
- `flat`: `bg-background text-foreground`
- `solid`: `bg-primary text-primary-foreground`

---

## 2. Item button — base

`flex flex-col items-center justify-center gap-0.5 flex-1 h-full min-w-0`
`text-xs font-medium transition-colors`
`focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-ring`
`disabled:opacity-40 disabled:cursor-not-allowed`

---

## 3. Item button — color by fill + selected state

| fill | selected | Classes |
|---|---|---|
| `flat` | true | `text-primary` |
| `flat` | false | `text-muted-foreground` |
| `solid` | true | `text-primary-foreground` |
| `solid` | false | `text-primary-foreground/60` — **fails WCAG AA; see G-BN4 below** |

### Gap G-BN4 — `fill="solid"` unselected label contrast (OPEN, earlier repository ticket #3396 finding)

Measured with the shipped tokens (`--color-primary` `#0f62fe` light / `#7ab8ff` dark;
`--color-primary-foreground` `#ffffff` light / `#111111` dark):

| theme | unselected (`/60`) | selected (full opacity) | AA floor (12px `text-xs`) |
|---|---|---|---|
| light | **2.73:1** ✗ | 5.00:1 ✓ | 4.5:1 |
| dark | **3.77:1** ✗ | 9.11:1 ✓ | 4.5:1 |

Raising the opacity cannot resolve it: in light theme even 100% opacity is 5.00:1, so any value
that clears 4.5:1 is visually indistinguishable from the selected state. A conformant solid fill
needs a different mechanism — a dedicated "muted-on-primary" token, or expressing selection with
weight/an indicator rather than opacity. That is a design decision; earlier repository ticket #3396 was bounded to
the selection-state ARIA pattern and deliberately took none. No `fill="solid"` story exists until
this is decided, because the axe gate would fail and a scoped waiver is not permitted.

---

## 4. Icon wrapper

`relative` — provides positioning context for badge.

---

## 5. Badge

`absolute -top-1 -right-1.5 min-w-[1rem] h-4 px-1`
`text-[10px] font-bold leading-4 text-center rounded-full`
`bg-destructive text-destructive-foreground`

---

## 6. Item text label

`truncate max-w-full` — rendered only when `item.text` is provided.
