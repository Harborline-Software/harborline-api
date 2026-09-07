# Card — Accessibility Contract

- **Component:** Card
- **ADR 0017 family:** Layout
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Card.Semantic.md) · [Interaction](./Card.Interaction.md) · [Styling](./Card.Styling.md)
- **Reference implementation:** _none yet — forward-spec; foundation per shadcn Card_
- **Catalog row:** #21 Card (`app-priority: high`, `library-scope: v1`, `Notes: shadcn Card`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

Card is structural and non-interactive by default. Its accessibility
contract is narrow but consequential: ensure the card's content is reachable
in source order, ensure the heading hierarchy under `CardTitle` is correct,
ensure the optional `aria-labelledby` wiring for landmark variants works, and
document the boundary where Card stops carrying responsibility (clickable-
card wrapper).

This contract pins:

1. The non-interactive default (no role; pass-through structural element).
2. The optional landmark role (`role="region"` + `aria-labelledby` when
   the consumer wants the Card as a navigation target).
3. The `CardTitle` heading-level guidance.
4. The clickable-card wrapper pattern and its accessibility responsibilities.

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. Root element + role

| Choice | Recommendation |
|---|---|
| Underlying element | Native `<div>` (block-level structural container) |
| Default ARIA role | NONE — Card is purely structural |
| `role="region"` | OPT-IN. When the consumer wants the Card to be a discoverable navigation landmark (e.g., a dashboard tile that a SR user benefits from jumping to), the consumer sets `as="section"` (rendering `<section>`) AND supplies `aria-labelledby` (typically pointing to the `CardTitle` id) |
| `aria-labelledby` | Required when `as="section"` is used; the labelled element gives the section its accessible name |
| `tabindex` | NEVER set on Card itself; cards are not focusable unless the entire surface is wrapped in `<a>`/`<button>` (see §6) |

**Default vs. landmark.** Most Cards are content tiles in a grid/list and
do NOT warrant a landmark role — adding `role="region"` to every card
creates landmark noise that disrupts SR navigation. The contract OPTS OUT
by default and OPTS IN only when the consumer has a deliberate reason.

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships — structural
information MUST be programmatically determined. The `<div>` baseline
satisfies this when the surrounding context (list, grid, region) carries
the structure; `<section>` + `aria-labelledby` raises the structure to
a landmark when needed.

---

## 3. Heading hierarchy under CardTitle

`CardTitle` carries the card's primary text. Its semantic heading level
MUST match the surrounding document outline.

| Context | Recommended heading level |
|---|---|
| Card is a top-level tile in a dashboard with no surrounding section header | `<h2>` |
| Card is inside a `<section>` with its own `<h2>` heading | `<h3>` |
| Card is inside a deeply nested context (sub-section, modal body) | `<h3>` or `<h4>` depending on outline |

**Implementation.** `CardTitle` MUST accept an `as` prop (or equivalent
`level` prop) so the consumer can pin the heading element:

```jsx
<CardTitle as="h3">Property Summary</CardTitle>
```

The default `as` value is engineer-domain (Semantic contract); recommended
default is `<h3>` (which fits most uses where Card lives under a section
heading).

**Anti-pattern.** Rendering `CardTitle` as a `<div>` with bold text
breaks the heading outline; SR users navigating by heading skip the card
entirely. NEVER ship `CardTitle` as a non-heading element by default.

**Inverse anti-pattern.** Rendering EVERY `CardTitle` as `<h2>` because
"it looks important" creates an outline where every card is at the same
level as a page section heading. The consumer MUST choose the level per
context.

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships; WCAG 2.2
SC 2.4.6 Headings and Labels.

---

## 4. CardDescription

`CardDescription` is supporting text below the title. No special ARIA
emissions required by default.

When Card is used as a labelled `<section>` (§2), the `aria-labelledby`
on the section MAY include both the `CardTitle` id AND the
`CardDescription` id (concatenation via space-separated ids):

```jsx
<section aria-labelledby="card-title-1 card-desc-1">
  <CardTitle id="card-title-1">Property Summary</CardTitle>
  <CardDescription id="card-desc-1">123 Main St — 2BR/1BA</CardDescription>
  ...
</section>
```

This is OPT-IN; default behaviour labels the section by title only.

---

## 5. Keyboard

Card is non-interactive — no keyboard behaviour at the Card level.

When Card is wrapped in `<a>` (clickable-card pattern, §6):

| Key | Behaviour | Where it's handled |
|---|---|---|
| Tab / Shift+Tab | Move focus to the wrapping `<a>` | Wrapper |
| Enter | Navigate to the `<a>`'s href | Wrapper |
| Space | (no default) | n/a — links don't activate on Space |

When Card is wrapped in `<button>`:

| Key | Behaviour | Where it's handled |
|---|---|---|
| Tab / Shift+Tab | Move focus to the wrapping `<button>` | Wrapper |
| Enter / Space | Activate the button | Wrapper |

Interactive children INSIDE a Card (buttons, links, form fields) carry
their own keyboard contracts. The clickable-card wrapper conflicts with
inner-interactive children — see §6 anti-pattern.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard.

---

## 6. Clickable card pattern

A Card MAY be wrapped in `<a href="...">` (link card) or `<button>`
(action card). The wrapper carries the interactive contract:

| Concern | Owned by Card? | Owned by wrapper? |
|---|---|---|
| Accessible name | partial — CardTitle text is read | wrapper provides composed name OR delegates to children's text |
| Focus ring | NO | YES — wrapper emits `focus-visible:ring-2` per Button-Accessibility §8 |
| Hover treatment | NO — Card stays variant-neutral | YES — wrapper applies `hover:shadow-md` or equivalent |
| Touch target | NO | YES — wrapper meets 24×24 minimum (typically far exceeded; cards are large) |
| `cursor: pointer` | NO | YES — wrapper applies on its `<a>` (or implicit on `<button>`) |
| Activation semantics | NO | YES — wrapper's `<a>` navigates; `<button>` activates |

### 6.1 Anti-pattern — interactive children inside a clickable card

A clickable card that ALSO contains interactive children (buttons,
links, form controls) creates a nested-interactive trap: the user
cannot reach the inner controls without the outer card stealing the
click. The contract FORBIDS this composition:

```jsx
// ANTI-PATTERN
<a href="/property/1">
  <Card>
    <CardBody>
      Address: 123 Main St
      <Button>Edit</Button>  {/* inner button can't be activated cleanly */}
    </CardBody>
  </Card>
</a>
```

Two acceptable resolutions:

a. **Make the title the link, not the card.** The Card is non-interactive;
   only the title is a link. Inner controls are free.

```jsx
<Card>
  <CardHeader>
    <CardTitle as="h3"><a href="/property/1">123 Main St</a></CardTitle>
  </CardHeader>
  <CardBody>
    Address details
    <Button>Edit</Button>
  </CardBody>
</Card>
```

b. **Card is a click target; no inner interactive children.** Useful for
   dashboard tiles where the entire tile navigates somewhere.

```jsx
<a href="/dashboard/properties" class="block focus-visible:ring-2 rounded-lg">
  <Card>
    <CardHeader><CardTitle as="h3">Properties</CardTitle></CardHeader>
    <CardBody>42 active</CardBody>
  </Card>
</a>
```

The Card contract names the rule; the Semantic contract MAY add a dev-
mode runtime warning when both patterns are detected together.

**WCAG citation:** WCAG 2.2 SC 2.5.7 Dragging Movements (no drag impact);
SC 2.1.1 Keyboard (the inner button MUST remain keyboard-activatable —
nested interactive elements break this in many SR/browser combinations).

---

## 7. Focus management

Card has no focus-management responsibility by default.

If Card is wrapped in an interactive element (link or button), the
wrapper's focus behaviour applies:

- Wrapper receives focus on Tab.
- Focus ring per the wrapper's own contract (Button-Accessibility §8 for
  button wrappers; analogous treatment for link wrappers).
- Focus stays on the wrapper after activation (until navigation occurs).

**WCAG citation:** WCAG 2.2 SC 2.4.7 Focus Visible.

---

## 8. Touch target

Card is non-interactive — touch-target requirements do NOT apply at the
Card level.

When wrapped in a clickable element, the entire card surface is the touch
target (typically far exceeding 24×24; SC 2.5.8 is trivially satisfied).

---

## 9. Color contrast

Per [Card.Styling §2](./Card.Styling.md) and the proposed
`layout.tokens.json` defaults:

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Body text on every variant's background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| CardTitle text on every variant's background | 4.5:1 | WCAG 2.2 SC 1.4.3 (and 3:1 for large text per SC 1.4.3 Exception) |
| CardDescription text on every variant's background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Card border on background (where border carries shape) | 3:1 | WCAG 2.2 SC 1.4.11 |
| Card separator (when shown) | 3:1 | WCAG 2.2 SC 1.4.11 |
| Shadow as visual cue | NOT subject to contrast rules | Shadow is decorative; primary boundary signal is border + radius |

The default token values in `layout.tokens.json` (proposed) MUST be pre-
verified. Provider overrides MUST re-verify.

**WCAG citation:** WCAG 2.2 SC 1.4.3 Contrast (Minimum); SC 1.4.11
Non-text Contrast.

---

## 10. Reduced motion

Card has NO animations by default. No reduced-motion handling required.

If a clickable-card pattern uses `transition-shadow` on hover, that
transition MUST honor `prefers-reduced-motion: reduce` — responsibility
lies with the wrapper, not Card.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 11. Do / Don't

### Do

- Pin `CardTitle` to the correct semantic heading level (`<h2>` / `<h3>`)
  per the surrounding outline.
- Pair body text foreground with the variant's background at ≥ 4.5:1.
- Use `as="section"` + `aria-labelledby` ONLY when the Card warrants
  landmark status.
- When making a card clickable, wrap the whole Card in `<a>` or `<button>`
  AND remove any inner interactive children (use the title-as-link
  alternative if you need inner actions).

### Don't

- Don't ship `CardTitle` as a non-heading element.
- Don't add `role="region"` by default. Landmark inflation harms SR
  navigation.
- Don't nest interactive elements inside a clickable card — pick one
  pattern.
- Don't put hover/focus/active visual states on Card itself. Those belong
  to the wrapping interactive parent.
- Don't use shadow as the only visual boundary signal — pair with border
  or radius for non-text-contrast safety.
- Don't drop `aria-labelledby` when using `as="section"`. Unnamed
  sections are landmark noise.

---

## 12. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** shadcn Card — `<div>` with
  variant + size CVA classes; named subcomponents `CardHeader` /
  `CardTitle` / `CardDescription` / `CardContent` (Body) / `CardFooter`.
- **Web Components (Phase M4, Lit):** TBD; Vaadin's `<vaadin-card>` slot
  pattern may inform the WC track.

---

## 13. Known gaps — forward-spec validation

Because Card is forward-spec, the following items MUST be validated when
the React implementation lands:

| # | Item | Validation criterion |
|---|---|---|
| F1 | `CardTitle` accepts `as` prop and renders the correct heading element | Snapshot test: `<CardTitle as="h3">X</CardTitle>` → `<h3>X</h3>` |
| F2 | Card default render emits `<div>` with NO ARIA role | Snapshot test |
| F3 | `as="section"` requires `aria-labelledby` (dev-mode warning if missing) | Dev-mode console-warning test |
| F4 | Clickable-card wrapper + inner button anti-pattern triggers dev-mode warning | Test rendering nested interactive; verify warning fires |
| F5 | Contrast verification for body text + title + description on every variant | Run axe scan against rendered Cards |
| F6 | Heading-outline test: a page containing several Cards has the expected outline shape | Run axe heading-order check; manual SR test |

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #21 Card (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- shadcn Card — reference foundation
- [Card.Semantic.md](./Card.Semantic.md) — prop contract
- [Card.Interaction.md](./Card.Interaction.md) — behavioural contract
- [Card.Styling.md](./Card.Styling.md) — token surface + visual states
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `region`, `aria-labelledby`
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.6 Headings and Labels
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.5.7 Dragging Movements
