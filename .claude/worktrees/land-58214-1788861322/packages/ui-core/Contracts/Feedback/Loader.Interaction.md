# Loader — Interaction Contract

- **Component:** Loader (and LoaderOverlay)
- **ADR 0017 family:** Feedback
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Loader.Semantic.md) · [Styling](./Loader.Styling.md) · [Accessibility](./Loader.Accessibility.md)
- **Reference implementation:** _not yet built_ — target `packages/ui-react/src/components/loaders/`
- **Catalog row:** #79 Loader (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)

---

## 1. Scope

Loader is **presentational and stateless**. Its interaction surface is the
empty set. This contract documents:

- The no-interaction baseline (§2).
- The LoaderOverlay scrim pointer-event behaviour (§3).
- Focus + keyboard behaviour during overlay-active state (§4).

---

## 2. No interaction surface (Loader)

The atomic `<Loader>` component:

- Has no `onClick`, `onFocus`, `onBlur`, or other callback as part of its
  public surface.
- Maintains no internal state.
- Is not focusable.
- Does not respond to keyboard input.

A Loader is pure visual + ARIA output. Its spinner animation runs on a
CSS keyframe loop independent of any user interaction.

---

## 3. LoaderOverlay scrim + pointer events

When `<LoaderOverlay active={false}>`:

- The wrapped children render normally with full pointer interaction.
- No overlay scrim, no spinner.

When `<LoaderOverlay active={true}>`:

- A semi-transparent scrim renders atop the wrapped children.
- The scrim captures pointer events (`pointer-events: auto`); the wrapped
  children become non-interactive (clicks land on the scrim, not on the
  underlying buttons / links / form fields).
- The scrim itself **does not fire any callback** on click. Clicks are
  silently absorbed — the user can see they clicked something but
  nothing happens. This is the deliberate "wait until loading completes"
  semantic.

### 3.1 Scrim click handling

- The contract does NOT include an `onScrimClick` callback in M2.
- Hosts who want "click anywhere to cancel the operation" wire their own
  click handler externally to the LoaderOverlay's root element via HTML
  attribute passthrough on the children container.
- This is a deliberate decision to keep LoaderOverlay simple — a blocking
  overlay should not also be a cancel action.

### 3.2 Active-state transitions

- **Inactive → active:** the scrim fades in (or appears instantly, per
  PAO Styling token); pointer interaction is blocked immediately on the
  React re-render.
- **Active → inactive:** the scrim fades out (or disappears); pointer
  interaction is restored. **In-flight clicks during the fade are
  absorbed** — once the host sets `active: true`, the scrim blocks until
  `active: false` is set; mid-fade does not re-enable pointer events.

---

## 4. Focus + keyboard behaviour

### 4.1 Loader (atomic)

- Loader is not focusable.
- Tab traversal skips Loader entirely.
- No keyboard handlers.

### 4.2 LoaderOverlay

**Inactive (`active: false`):**
- Tab traversal through the wrapped children is normal.

**Active (`active: true`):**
- The scrim is NOT focusable.
- The wrapped children are NOT focus-trapped — Tab traversal continues to
  flow through them naturally, and through other elements on the page.
  BUT: the children are visually obscured + non-clickable, so tabbing to
  a hidden button is mostly inert.
- **Council open question 1** considers whether LoaderOverlay should
  trap focus (preventing Tab into the blocked region) or just block
  pointer events.
- PAO Accessibility owns the `aria-busy="true"` and `aria-live` wiring
  on the wrapped region so AT users hear that the region is loading.

---

## 5. State change behaviour

### 5.1 Loader prop changes

Changes to `size`, `variant`, `label`, `inline` are pure re-renders. No
animation choreography.

### 5.2 LoaderOverlay `active` flips

- `false → true`: scrim renders; PAO Styling owns whether to animate the
  appearance.
- `true → false`: scrim hides; PAO Styling owns the exit animation (if
  any).
- The wrapped `children` remain mounted across the active flip — their
  state is preserved (DataGrid scroll position, form input values,
  etc.).

---

## 6. Keyboard behaviour

| Key | Context | Behaviour |
| --- | --- | --- |
| Tab / Shift+Tab | Loader (any) | Skips; not in tab order. |
| Tab / Shift+Tab | LoaderOverlay inactive | Normal children traversal. |
| Tab / Shift+Tab | LoaderOverlay active | Currently: passes through children (visually obscured but in tab order). Council open question 1 considers focus trap. |
| ESC | LoaderOverlay active | No effect in M2 (no cancel action). Council open question 2 considers ESC-to-cancel hook. |

---

## 7. Interaction-state precedence

For Loader: no states.

For LoaderOverlay:

1. **`active === true`** — scrim captures pointer events; wrapped
   children are non-interactive even if focused.
2. **`active === false`** — wrapped children interact normally.

---

## 8. Council open questions (Interaction)

1. **Focus trap in active LoaderOverlay.** Today: no trap. Tab moves
   focus through obscured children. Should LoaderOverlay trap focus
   (similar to Dialog's focus trap) so Tab cycles between… nothing?
   Or skips the wrapped region entirely until inactive? Leaning
   **skip wrapped region** — focus moves past LoaderOverlay onto the
   next page element. PAO Accessibility decides.
2. **ESC = cancel hook.** Should LoaderOverlay accept an
   `onCancelRequest?: () => void` that fires when the user presses
   ESC while the overlay is active? Leaning yes — cheap to add,
   addresses the "user wants out" need. Defer to M3 if council prefers.
3. **Scrim click → onCancelRequest.** Pair with ESC. Today: scrim
   click is silently absorbed. Leaning add the symmetry (ESC and
   scrim click both fire `onCancelRequest` if supplied).
4. **Animated state changes.** This contract leaves animation choices
   to PAO Styling. Should the behavioural contract require fade-in
   on appearance? Leaning **no** — behaviour is "scrim appears /
   disappears"; how is PAO's call.
5. **Delayed render.** Should LoaderOverlay accept a `delay?: number`
   prop to defer the scrim render by N ms (avoiding flashes for fast
   operations)? Leaning yes — common quality-of-life feature.
   Confirm timing default: 150ms is canonical.
6. **`aria-busy` placement.** When LoaderOverlay is active, where does
   `aria-busy="true"` go — on the wrapped region (preferred per ARIA
   spec) or on the document body? Leaning wrapped region; PAO
   Accessibility confirms.
