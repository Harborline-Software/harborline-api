# TabStrip — Interaction Contract

- **Component:** TabStrip
- **ADR 0017 family:** Navigation
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TabStrip.Semantic.md) · [Styling](./TabStrip.Styling.md) · [Accessibility](./TabStrip.Accessibility.md)
- **Reference implementation:** _not yet built_ — target `packages/ui-react/src/components/navigation/TabStrip.tsx`
- **Catalog row:** #131 TabStrip (`app-priority: high`, `library-scope: v1`, `Radix/shadcn: ✓ Radix Tabs`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)

---

## 1. Scope

TabStrip wraps Radix UI's Tabs primitive; most interaction behaviour is
delegated to Radix. This contract documents:

- Tab activation triggers and the controlled-state lifecycle (§2).
- Keyboard navigation (orientation-aware) (§3).
- The `activationMode` semantic (automatic vs manual) (§4).
- Disabled-tab handling (§5).
- Panel mount/unmount lifecycle and `forceMount` (§6).

---

## 2. Tab activation

### 2.1 Triggers

- **Click / tap** on a `<Tab>` — activates that tab.
- **Enter / Space** when a `<Tab>` has focus — activates that tab.
- **Arrow keys** when a `<Tab>` has focus and `activationMode === 'automatic'` —
  moves focus AND activates the focused tab.
- **Arrow keys** when `activationMode === 'manual'` — moves focus only;
  the user must press Enter or Space to commit.

### 2.2 Callback chain

On activation:

1. Radix internally updates focus and selection state.
2. TabStrip's `onValueChange(value)` fires with the newly active tab's
   `value`.
3. Host updates `value`; TabStrip re-renders with the new active tab
   highlighted and the corresponding panel visible.

### 2.3 Activation of the already-active tab

Re-activating the currently-active tab fires `onValueChange` again
(Radix-canonical) with the same value. Hosts that want to suppress
re-renders should guard their state update (`if (next === current) return`).

---

## 3. Keyboard behaviour

Keys are orientation-aware (Radix handles this; the contract names the
behaviour):

### 3.1 Horizontal orientation

| Key | Behaviour |
| --- | --- |
| Tab | Moves focus into the TabList (lands on the active tab) or out to the next focusable element after the TabList. |
| Shift+Tab | Reverse of Tab. |
| Left | Moves focus to the previous tab (wraps to last from first per Radix default). |
| Right | Moves focus to the next tab (wraps to first from last). |
| Home | Moves focus to the first tab. |
| End | Moves focus to the last tab. |
| Enter / Space | Activates the focused tab (always, regardless of activation mode). |

### 3.2 Vertical orientation

| Key | Behaviour |
| --- | --- |
| Up | Previous tab (wraps). |
| Down | Next tab (wraps). |
| Home / End | First / last tab. |
| Enter / Space | Activates. |

Left/Right are inert in vertical orientation; Up/Down are inert in
horizontal orientation.

### 3.3 Inside a `<TabPanel>`

Standard Tab traversal applies — focus moves through interactive
descendants of the active panel. There is no special "exit panel" key.
TabStrip does not capture keys inside its panels.

---

## 4. `activationMode` semantics

### 4.1 `'automatic'` (default)

- Arrow-key focus move = activation. Each Left / Right (or Up / Down)
  press both moves focus and fires `onValueChange` for the newly
  focused tab.
- Suits panels that are cheap to render (text content, simple forms).
- Standard for ARIA Authoring Practices' "tabs with automatic
  activation" pattern.

### 4.2 `'manual'`

- Arrow-key focus move = focus only. The active tab does not change
  until the user presses Enter or Space.
- Suits panels that are expensive to render (data tables, charts) where
  rapid arrow-key traversal would thrash mounts.
- Standard for ARIA Authoring Practices' "tabs with manual activation"
  pattern.

---

## 5. Disabled tabs

When `<Tab disabled>`:

- Click / Enter / Space do not activate the tab.
- Arrow-key navigation **skips** the disabled tab (Radix behaviour).
  Pressing Right from the tab immediately before a disabled tab moves
  to the tab immediately after.
- The disabled tab is **not focusable** by Tab (Radix omits it from
  the focus order).
- PAO Accessibility owns whether to keep the disabled tab visually
  visible (greyed out) vs hidden.

---

## 6. Panel mount lifecycle

### 6.1 Default (unmount on inactive)

Radix unmounts inactive `<TabPanel>` content. When the user switches
tabs:

- The previously-active panel **unmounts** — its React tree disposes.
  Any local state inside the panel is lost (unless lifted to the host).
- The newly-active panel **mounts** — its React tree initialises fresh.

For most panels (display-only or short forms), this is correct: tabs
are cheap to switch, no state leak.

### 6.2 `forceMount` (panel-level opt-in)

When `<TabPanel forceMount>`:

- The panel is **always mounted** regardless of activity.
- Inactive panels are hidden via CSS (`hidden` attribute or
  `display: none` per Radix).
- Mount happens once on first render; unmount happens only on TabStrip
  unmount.

Use cases:

- Panels with expensive setup (e.g. a chart that re-fetches data on
  every mount).
- Panels whose internal state must survive tab switches (e.g. a
  multi-step form within a tab).

The `forceMount` opt-in is **per-panel**, not TabStrip-wide.

### 6.3 TabList mount

TabList and all `<Tab>` instances are always mounted. They are
inexpensive (just buttons) and their state is owned by Radix's root.

---

## 7. Focus behaviour

### 7.1 Initial focus

- TabStrip does not auto-focus anything on mount. The tab buttons are
  in the tab order; focus lands on the active tab when the user Tabs
  into the TabList.

### 7.2 On value change

- When `value` changes via host update (e.g. URL change), TabStrip does
  **not** automatically move focus to the new active tab. Focus stays
  wherever the user put it.
- When the user activates a tab via click / keyboard, focus naturally
  stays on the activated tab (Radix default).

### 7.3 Focus when active tab is disabled mid-session

If the host sets `value` to a tab that is `disabled`, TabStrip's
behaviour is **undefined** in M2 (council open question 1 considers).
Hosts should not do this; if they must, the disabled tab visually
activates but is non-focusable.

---

## 8. Variant change behaviour

Changes to `variant` or `orientation` are pure re-renders. There are no
animations or transitions defined at the contract level.

---

## 9. Interaction-state precedence

When multiple states apply, resolve in this order (highest precedence
first):

1. **Disabled tab** — non-activatable; arrow-key navigation skips.
2. **Active tab** — Visual active treatment; panel is rendered (or
   visible if `forceMount`).
3. **Focus / hover** — additive visual states.
4. **Normal (inactive, not focused)** — default rendering.

---

## 10. Council open questions (Interaction)

1. **Active tab becomes disabled mid-session.** What happens if the
   host sets `value` to a tab that has just become `disabled`?
   Leaning: the tab still renders as "active" visually but is
   non-focusable; PAO Accessibility decides whether to also fire a
   warning. Hosts should treat this as a bug in their own state.
2. **Default `activationMode`.** `'automatic'` (this spec, Radix
   default) or `'manual'`? Some accessibility advocates prefer manual
   for predictability; some panels (data tables) benefit from manual
   to avoid thrashing mounts. Leaning automatic — Radix default; hosts
   opt-in to manual when needed.
3. **Wraparound arrow navigation.** Radix's default wraps (Right from
   last tab → first tab). Should we suppress this for very wide
   TabStrips where wrap is visually confusing? Leaning keep wrap —
   standard ARIA pattern.
4. **Tab activation while a panel transition is animating.** If a host
   wraps `<TabPanel>` in a framer-motion entrance animation, what
   happens if the user activates another tab mid-animation? Today: the
   animation aborts; the new panel mounts immediately. Hosts that want
   coordinated exit-then-enter must handle externally.
5. **Mouse activation behaviour while keyboard focus is on a different
   tab.** Today: clicking a tab activates it AND moves focus to it.
   This is Radix default and correct for most cases. Confirm.
6. **`onValueChange` payload completeness.** Today: `string` (the value
   only). Some teams want `(value, previousValue, source: 'keyboard' |
   'mouse')`. Leaning keep minimal — extras can join the payload later
   non-breakingly.
