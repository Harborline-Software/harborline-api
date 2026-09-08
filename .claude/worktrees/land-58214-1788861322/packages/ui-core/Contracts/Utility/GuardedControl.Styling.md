# GuardedControl — Styling Contract

- **Component:** GuardedControl
- **ADR 0017 family:** Utility
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./GuardedControl.Semantic.md) · [Interaction](./GuardedControl.Interaction.md) · [Accessibility](./GuardedControl.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/guards/GuardedControl.tsx`
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

GuardedControl has two visual faces — **covered** (a neutral, "under glass" resting state) and
**armed/committing** (a live, primary-accented exposed control with a countdown). This contract names
the semantic token utilities each state uses. Per the token-discipline doctrine (§8.1), the component
uses **semantic colour-token utilities only** (no raw hex, no arbitrary Tailwind shades); the concrete
values live in the design-token layer, not here or in the `.tsx`.

---

## 2. Token surface (semantic utilities)

### 2.1 Covered state — neutral "under glass"

| Region | Utility | Role |
|---|---|---|
| Surface | `bg-muted` | Recessed neutral — reads as "not the primary action right now". |
| Border | `border border-border` | Quiet delineation. |
| Label + icon | `text-muted-foreground` | De-emphasised (the action is dormant). |
| Hover | `hover:bg-accent hover:text-accent-foreground` | Affordance that it is activatable. |
| Focus | `focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2` | Visible focus ring (WCAG 2.4.7). |
| Disabled | `disabled:opacity-50 disabled:cursor-not-allowed` | Accessible denial. |

The covered control carries a **lock** glyph — the universal "guarded" signal, redundant with the
SR-announced name (dual channel, WCAG 1.4.1: the state is not colour-only).

### 2.2 Armed / committing state — live primary control

| Region | Utility | Role |
|---|---|---|
| Surface | `bg-primary` | Brand-primary — this is now THE action; it stands out from the covered rest state. |
| Border | `border border-primary` | — |
| Label + icon | `text-primary-foreground font-semibold` | High-emphasis; an **unlock** glyph replaces the lock. |
| Hover | `hover:bg-primary/90` | — |
| Focus | `focus-visible:ring-2 focus-visible:ring-ring focus-visible:ring-offset-2` | — |
| Countdown badge | `bg-primary-foreground/20 text-xs tabular-nums` | Subtle inset badge; `tabular-nums` keeps the seconds from jitter-shifting. |
| Committing | `disabled:opacity-70` | Inert while `onCommit` resolves. |

### 2.3 Heavy-composition deferral state

| Region | Utility | Role |
|---|---|---|
| Button | `bg-muted border-border text-muted-foreground opacity-70` (disabled) | Clearly inert. |
| Note | `text-xs text-muted-foreground` | The "not available yet" explanation. |

---

## 3. Motion

The covered↔armed transition uses `transition-all duration-200` **unless** `prefers-reduced-motion:
reduce` is set, in which case the transition classes are omitted (instant swap). No transform-based
flip is animated in M1 — the state swap is a class change; the "flip" is expressed through the
lock→unlock glyph + surface change. A richer 3D cover-flip is a deferred visual enhancement (it must
keep the reduced-motion instant-swap fallback).

**WCAG:** SC 2.3.3 Animation from Interactions.

---

## 4. Visual state inventory

| State | Condition | Surface | Glyph |
|---|---|---|---|
| **covered** | resting | `bg-muted` / `text-muted-foreground` | lock |
| **covered hover** | pointer/focus over covered | `bg-accent` / `text-accent-foreground` | lock |
| **covered disabled** | `disabled` | `bg-muted opacity-50` | lock |
| **armed** | after arm, before fire | `bg-primary` / `text-primary-foreground` + countdown badge | unlock |
| **committing** | `onCommit` in flight | `bg-primary opacity-70` | unlock |
| **deferred** | heavy composition | `bg-muted opacity-70` + note | lock |

---

## 5. Do / Don't

### Do
- Consume the semantic token utilities above; keep the `.tsx` token-pure (the token-discipline lint
  enforces this on the ui-react styling surface).
- Keep the lock/unlock glyph — it is the colour-independent channel for the guarded/armed state.
- Honour `prefers-reduced-motion` (the reduced-motion branch already omits the transition).

### Don't
- Don't put raw hex or arbitrary Tailwind shades (`bg-blue-600`, `text-[13px]`) in the component —
  use semantic tokens (`bg-primary`, `text-primary-foreground`, …).
- Don't style the armed state as neutral — the whole point is that the exposed control reads as THE
  live action (primary accent), distinct from the covered rest state.
- Don't animate a flip without a reduced-motion instant-swap fallback.

---

## 6. Parity notes

- **Blazor (future track):** consumes the same semantic token families (`bg-muted`, `bg-primary`, …)
  through the Blazor token bridge.
- **React (this contract):** as documented.

---

## References

- ADR 0017 §A1 — Utility family contract scope
- Harborline Design Language doctrine §8.1 — token-discipline (semantic utilities only)
- `_shared/design/first-run-and-workshop-ia-design-2026-07-06.md` §AD.2
- WCAG 2.2 SC 1.4.1 Use of Color · SC 1.4.11 Non-text Contrast · SC 2.3.3 Animation from Interactions · SC 2.4.7 Focus Visible
