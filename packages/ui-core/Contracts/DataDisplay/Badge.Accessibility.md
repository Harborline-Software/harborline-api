# Badge — Accessibility Contract

- **Component:** Badge
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Badge.Semantic.md) · [Interaction](./Badge.Interaction.md) · [Styling](./Badge.Styling.md)
- **Reference implementation:** _none yet — forward-spec; foundation per shadcn Badge_
- **Catalog row:** #9 Badge (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

Badge is presentational and non-interactive by default. Its accessibility
contract is therefore narrow: ensure the badge's content is reachable by
AT, ensure state variants don't rely on colour alone, and document the
boundary where Badge stops carrying responsibility (interactive parent).

This contract pins:

1. The non-interactive default (no role, no focusable element).
2. The cross-channel signal rule for state variants.
3. The screen-reader behaviour for inline-icon + count + status patterns.
4. The boundary against Badge-composed-inside-an-interactive-parent.

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. Root element + role

| Choice | Recommendation |
|---|---|
| Underlying element | Native `<span>` (inline) |
| Default ARIA role | NONE (no role attribute). Badge is presentational text — adding `role="status"` or `role="img"` introduces SR announcements that are not warranted at the badge scale |
| `aria-label` on the badge | NOT default. The visible text content IS the accessible name |
| `tabindex` | NEVER set on Badge itself (badges are not focusable) |

**Special case — count badges that update dynamically.** When a Badge
represents a value that changes during the session (e.g., "3 unread"
notifications), the badge MAY be wrapped in or have applied an `aria-live`
region so AT announces the change. The live region is OPTIONAL and OFF
by default; the consumer opts in via prop (`announceChanges?: boolean`).
See §6.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value — `<span>` carries
no implicit role; the badge text is read as inline text in the surrounding
flow.

---

## 3. Accessible name

The badge's visible text content IS the accessible name. No additional
ARIA wiring is required for the default presentation.

| Badge shape | Name source |
|---|---|
| Text-only: `<Badge>Active</Badge>` | "Active" (visible text) |
| Icon + text: `<Badge><CheckIcon aria-hidden /> Active</Badge>` | "Active" (visible text; icon decorated as hidden) |
| Numeric: `<Badge>12</Badge>` | "12" (read as a number by SR) |
| Numeric with unit context: `<Badge aria-label="12 unread messages">12</Badge>` | "12 unread messages" — used when the context around the number is not visible adjacent to the badge |

**Forbidden patterns:**

- Decorative inline icon WITHOUT `aria-hidden="true"` → SR announces the
  icon name (e.g., "checkmark Active") which is verbose.
- `aria-label` that duplicates the visible text → double-speaking.
- Badge with NO visible text and NO `aria-label` → invisible to SR.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 4. Cross-channel signal — state variants

The state variants (`success` / `warning` / `danger` / `info`) use colour
as a visual differentiator. WCAG 2.2 SC 1.4.1 Use of Color forbids colour
as the sole channel.

**REQUIREMENT.** When `variant` is a state variant, the badge label text
MUST carry the semantic meaning:

| Variant | Required label semantics | Allowed labels |
|---|---|---|
| `success` | Positive-state noun/verb | "Active", "Paid", "Online", "Approved", "Completed" |
| `warning` | Caution noun/verb | "Pending", "Overdue", "At risk", "Expiring" |
| `danger` | Failure / negative-state noun/verb | "Failed", "Expired", "Offline", "Rejected", "Cancelled" |
| `info` | Informational/new noun | "New", "Draft", "Beta", "Updated" |
| `default` / `secondary` | No content restriction | (any) |

Implementations MAY emit a dev-mode console warning when a state-variant
badge has empty or whitespace-only content.

**WCAG citation:** WCAG 2.2 SC 1.4.1 Use of Color.

---

## 5. Keyboard

Badge is non-interactive — no keyboard behaviour at the Badge level.

When Badge is composed inside an interactive parent (e.g.,
`<button><Badge>3</Badge> Notifications</button>`):

| Key | Behaviour | Where it's handled |
|---|---|---|
| Tab / Shift+Tab | Move focus to the parent | Parent (`<button>` / `<a>`) |
| Enter / Space | Activate the parent | Parent |
| Arrow keys | n/a — Badge does not bind arrows | n/a |

Badge MUST NOT be focusable on its own. Setting `tabindex="0"` on a Badge
violates the contract (Badge has no activation behaviour to expose).

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard — Badge contributes no
keyboard surface; the surrounding interactive element (if any) carries
the keyboard contract.

---

## 6. Dynamic count announcements (opt-in)

When Badge represents a value that may change during the session, the
consumer MAY opt into live-region announcements:

| Prop | Default | Behaviour when opted in |
|---|---|---|
| `announceChanges?: boolean` | `false` | When `true`, Badge wraps its content in `<span aria-live="polite" aria-atomic="true">{children}</span>` so AT announces changes |

**Polite vs. assertive.** Default is `polite` — count changes are non-
disruptive. If the badge represents an urgent state (e.g., "3 errors"
that the user MUST address before continuing), the consumer MAY wrap the
badge in their own `aria-live="assertive"` region — the Badge component
does NOT expose an `assertive` mode because that's a context-specific
override.

**Atomicity.** `aria-atomic="true"` ensures the entire badge content is
re-announced when any part changes (rather than just the diff).

**Cadence.** AT typically rate-limits live-region announcements; rapid
successive changes (e.g., a counter ticking from 1 → 2 → 3 within 100ms)
will collapse into a single announcement of the final value.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages.

---

## 7. Composition inside interactive parents

When Badge is composed inside a button/link/menu-item, the contract
boundary shifts:

| Concern | Owned by Badge? | Owned by parent? |
|---|---|---|
| Hover background | NO | YES |
| Focus ring | NO | YES |
| Click activation | NO | YES |
| Cursor pointer | NO | YES |
| Accessible name composition | partial — Badge text is part of the accessible name | parent reads concatenated text content |
| Cross-channel signal (state variant + label) | YES — Badge enforces this regardless of parent | — |

**Pattern: Notification button with unread count.**

```jsx
<button aria-label={`Notifications, ${count} unread`}>
  <BellIcon aria-hidden="true" />
  <Badge variant="danger">{count}</Badge>
</button>
```

The button supplies the full accessible name via `aria-label`; the
Badge's visible "3" is decorative for SR users in this composition
because the `aria-label` overrides accessible-name calculation. The
visual badge still serves sighted users.

**Pattern: Tag chip (interactive Badge).** OUT OF M2 SCOPE — that
introduces hover/focus/active states on the badge surface which Badge
does NOT support. A future Chip component would carry that contract.

---

## 8. Focus management

Badge has no focus-management responsibility. It is never focusable, and
it does not move focus on mount/unmount.

If a Badge appears/disappears dynamically (e.g., notification count goes
from 0 to 3 and the badge mounts), the live-region announcement (§6, opt-
in) is the SR notification channel. Focus does NOT move to the new badge.

**WCAG citation:** WCAG 2.2 SC 3.2.2 On Input — Badge does not cause a
context change.

---

## 9. Touch target

Badge is non-interactive — touch-target requirements do NOT apply at the
Badge level.

If the surrounding interactive parent contains the Badge, the parent
carries the touch-target obligation (Button: 24×24 minimum per
WCAG 2.2 SC 2.5.8; see Button.Accessibility §9).

---

## 10. Color contrast

Per [Badge.Styling §2](./Badge.Styling.md) and the
`data-display.tokens.json` (proposed addition) defaults:

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Badge label on badge background (every variant) | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Badge border on badge background (where border carries shape) | 3:1 | WCAG 2.2 SC 1.4.11 |

The default token values in `data-display.tokens.json` MUST be pre-
verified. Provider overrides MUST re-verify.

**Color is not the only channel.** State variants carry meaning via both
colour AND label (§4). Provider overrides that swap the colour palette
do NOT change the label requirement — the cross-channel rule is
permanent.

**Inline-icon-only badges.** NOT supported — Badge MUST always carry
text content (see §3).

**WCAG citation:** WCAG 2.2 SC 1.4.1 Use of Color.

---

## 11. Reduced motion

Badge has no animations by default. No reduced-motion handling required.

If a future enhancement adds a pulse-on-mount animation for "new" badges,
that MUST honor `prefers-reduced-motion: reduce` per WCAG 2.2 SC 2.3.3.

---

## 12. Do / Don't

### Do

- Let the visible text content carry the accessible name.
- Decorate inline icons with `aria-hidden="true"`.
- Pair state variants with semantic labels ("Active" not just "✓").
- Opt into `announceChanges` when the badge's value updates dynamically
  AND the change is meaningful enough to warrant a SR announcement.
- For composed patterns (badge inside button), let the button own focus,
  hover, click, and accessible name; Badge contributes only its text.

### Don't

- Don't set `tabindex` on Badge. Badge is never focusable.
- Don't add `role="status"` or `role="img"` to Badge. The default
  presentation is correct.
- Don't use colour alone for state — the label MUST carry the meaning.
- Don't compose hover/focus styles on Badge. If the parent is
  interactive, the parent owns those.
- Don't drop the label and ship an icon-only Badge. SR users have
  nothing to announce.
- Don't use `aria-label` that duplicates the visible text.
- Don't use `aria-live="assertive"` from inside Badge — let the consumer
  wrap a polite badge in an assertive context if their use case demands.

---

## 13. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** shadcn Badge — `<span>` with
  variant + size CVA classes; no ARIA emissions.
- **Web Components (Phase M4, Lit):** TBD; the WC track adopts whichever
  baseline is canonical at M4 entry.

---

## 14. Known gaps — forward-spec validation

Because Badge is forward-spec, the following items MUST be validated when
the React implementation lands:

| # | Item | Validation criterion |
|---|---|---|
| F1 | `announceChanges` correctly wraps content in `aria-live="polite"` | E2E test: mutate badge value, verify SR announcement fires |
| F2 | State-variant label invariant (dev-mode warning) does not fire false-positives in non-English locales | Test with i18n labels in fr-FR, de-DE, ja-JP |
| F3 | Decorative inline icons are emitted with `aria-hidden="true"` by default | Manual SR test: SR reads only the label, not the icon |
| F4 | Badge composed inside a button does NOT receive double-announcement | Manual SR test: `<button aria-label="..."><Badge>3</Badge></button>` reads only the button's aria-label |
| F5 | Contrast verification for every variant (light theme) | Run axe scan against rendered badges |
| F6 | Badge is rendered as `<span>`, NOT `<div>` — preserves inline flow | Snapshot test |

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #9 Badge (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- shadcn Badge — reference foundation
- [Badge.Semantic.md](./Badge.Semantic.md) — prop contract
- [Badge.Interaction.md](./Badge.Interaction.md) — behavioural contract
- [Badge.Styling.md](./Badge.Styling.md) — token surface + visual states
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 3.2.2 On Input
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 4.1.3 Status Messages
