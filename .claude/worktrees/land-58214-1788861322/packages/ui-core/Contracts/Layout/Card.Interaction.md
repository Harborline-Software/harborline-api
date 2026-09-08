# Card — Interaction Contract

- **Component:** Card
- **ADR 0017 family:** Layout
- **Contract type:** Interaction (behavior, state transitions, input handling)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Card.Semantic.md) · [Styling](./Card.Styling.md) · [Accessibility](./Card.Accessibility.md)
- **Reference implementation:** _not yet built_ — target `packages/ui-react/src/components/cards/Card.tsx`
- **Catalog row:** #21 Card (`app-priority: high`, `library-scope: v1`, `Notes: shadcn Card`)
- **Phase:** ADR 0017-A1 Phase M2 (forward-spec; component not yet implemented)

---

## 1. Scope

Card is a **structural container**. Its baseline interaction surface is the
empty set — no events, no state, no keyboard handling. The single interaction-
adjacent feature is the `asChild` clickable-card composition (Semantic §3.7),
which delegates entirely to the wrapped interactive element.

This contract documents:

- The no-interaction baseline (§2).
- The `asChild` clickable-card pattern (§3).
- The composition boundary with interactive descendants (§4).

---

## 2. No interaction surface (baseline)

When Card is used in its default (non-`asChild`) form:

- The root `<div>` is not focusable.
- No `onClick` / `onFocus` / `onBlur` / `onKeyDown` is part of the public
  surface (though HTML attribute passthrough allows hosts to add them at
  their own risk — see §6).
- No internal state machine, hover state, or animation is required by the
  contract.

Interactive descendants (Buttons inside CardFooter, links inside
CardContent, etc.) handle their own activation independently. Card adds
nothing to or removes nothing from their behaviour.

---

## 3. `asChild` clickable-card pattern

When `<Card asChild>` wraps a single interactive child (`<a>`, `<button>`,
routing `<Link>`):

- **Activation triggers** delegate to the wrapped child's native semantics:
  - `<a href=…>` activates on click or Enter (no Space).
  - `<button>` activates on click or Enter or Space.
  - A custom-component child uses its own semantics.
- **Focus:** the wrapped child is the focusable element. Card itself adds
  no tab-stop.
- **Hover:** the wrapped child's hover state cascades to Card's styling
  (PAO Styling owns the hover token — e.g. a subtle elevation increase).
- **Disabled state:** if the wrapped child is `disabled`, the card is
  non-activatable; PAO Accessibility decides whether to swap to
  `aria-disabled` for `<a>` (no native `disabled` on anchors).

The clickable card is the **only** interactive form of Card in M2.

---

## 4. Interactive descendants — event bubbling

A common composition pattern raises a subtle question:

```tsx
<Card asChild>
  <a href="/property/123">
    …
    <CardFooter>
      <Button onClick={handleArchive}>Archive</Button>
    </CardFooter>
  </a>
</Card>
```

Clicking the Archive button bubbles its click event through the `<a>`,
which then navigates. **Card does not intercept this bubble.** Hosts who
need the button to suppress navigation must:

- Call `e.stopPropagation()` in the Button's `onClick`, or
- Restructure so the button is outside the clickable card region.

This is a deliberate composition trade-off (Semantic §8 open question 6).
The contract takes no position on which pattern is "correct"; it just
documents that Card itself does not solve the conflict.

---

## 5. Keyboard behaviour

- **Default (non-`asChild`) Card:** no keyboard contribution; the card is
  not in the tab order.
- **`asChild` Card wrapping an interactive element:** the wrapped element
  contributes its own tab-stop. Keyboard activation follows that element's
  native semantics (anchor: Enter; button: Enter or Space).
- **Interactive descendants (Buttons, links inside CardContent):**
  contribute their own tab-stops independently. Tab order follows DOM
  order top-to-bottom within the card.

Card adds no custom keyboard handlers.

---

## 6. HTML-attribute pass-through and accidental interactivity

Card spreads HTML attributes onto the root element (Semantic §3.6). A host
can pass `onClick`, `role="button"`, `tabIndex="0"`, etc., and the native
browser will honour them.

This contract takes **no position** on whether such host-applied
interactivity is supported behaviour. The recommended pattern is to use
`asChild` with a wrapping `<a>` / `<button>` instead, which makes the
semantic intent explicit and lets PAO Accessibility wire focus / ARIA
correctly. Hosts who add `onClick` directly to Card own the
accessibility consequences.

---

## 7. State changes (re-renders)

Changes to `elevation`, `padding`, or `asChild` are pure re-renders. There
are no internal animations or transitions defined at the contract level
(PAO Styling may add a hover-elevation transition for clickable cards;
the behavioural contract does not require one).

A Card whose `elevation` changes from `'outlined'` to `'raised'` simply
re-renders with the new tokens.

---

## 8. Loading / busy state

Card has **no loading state**. When a card's content is loading, the
canonical pattern is:

```tsx
<Card>
  <CardHeader>
    <CardTitle>Outstanding invoices</CardTitle>
  </CardHeader>
  <CardContent>
    {isLoading ? <Loader /> : <DataGrid {...} />}
  </CardContent>
</Card>
```

Card itself stays static — the swap happens inside CardContent.

A future enhancement (Semantic §7 deferred) may add a `loading` prop that
overlays a Loader on the entire card surface. Out of scope for M2.

---

## 9. Interaction-state precedence

For default (non-`asChild`) Card: no states to order.

For `asChild` Card wrapping an interactive child:

1. **Child `disabled`** — the wrapping interactive element is non-
   activatable; click on Card surface fires nothing.
2. **Normal** — wrapping child is activatable; click on Card surface
   activates it.

Hover treatment (PAO Styling) follows the same precedence — disabled
state suppresses the hover-elevation token.

---

## 10. Council open questions (Interaction)

1. **Event-bubble interception for `asChild` cards with interactive
   descendants.** Should Card auto-`stopPropagation` on click events
   coming from descendants matching `[role="button"], button, a`?
   Leaning no — too magical, breaks composition predictability. Hosts
   handle.
2. **Should non-`asChild` Card support a host-applied `onClick`?** This
   contract documents "not advertised behaviour". Should we go further
   and actively warn (dev-mode) when a host passes `onClick` to a
   non-`asChild` Card? Leaning no — warns are noisy; documentation is
   enough.
3. **Clickable-card hover elevation.** PAO Styling will likely raise the
   elevation on hover for `asChild` cards. Should this contract require
   that, or leave entirely to PAO? Leaning **leave to PAO** — it's a
   visual choice, not a behavioural one.
4. **Sub-component interactive variants.** Should `<CardHeader>` /
   `<CardFooter>` ever support their own `asChild`? Leaning **no** —
   would multiply the surface without clear use cases; the wrapping-
   anchor pattern handles the whole-card-link case.
