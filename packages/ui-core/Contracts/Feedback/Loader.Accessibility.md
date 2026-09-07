# Loader — Accessibility Contract

- **Component:** Loader
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Loader.Semantic.md) · [Interaction](./Loader.Interaction.md) · [Styling](./Loader.Styling.md)
- **Reference implementation:** _none yet — forward-spec_
- **Catalog row:** #79 Loader (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

Loader's accessibility responsibility is narrow but consequential: pair the
visual indicator with a programmatic signal so non-sighted users know an
operation is in flight. Done incorrectly, the loader is invisible to SR
users; done correctly, the live-region announcement is timely and non-
disruptive.

This contract pins:

1. The `role="status"` (spinner / dots) vs. `role="progressbar"` (bar)
   role choice.
2. The `aria-busy="true"` requirement on the containing region.
3. The `aria-label` / `aria-labelledby` accessible-name patterns.
4. The reduced-motion fallback that preserves the SR signal even when
   visual animation is suppressed.
5. The defer-timing recommendation (≥ 300-500ms) that prevents flash-on /
   flash-off cycles.

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. Role + aria-busy pairing

The accessibility implementation has TWO complementary channels:

### 2.1 On the Loader element itself

| Variant | Role | Notes |
|---|---|---|
| `spinner` | `status` | Polite live region; SR announces label when speech idle |
| `dots` | `status` | Polite live region |
| `bar` | `progressbar` | Indeterminate; emit `aria-valuetext="In progress"` (or i18n equivalent); MAY emit `aria-valuemin="0" aria-valuemax="100"` without `aria-valuenow` to formally mark as indeterminate |

### 2.2 On the containing region

The region that's currently loading content MUST emit `aria-busy="true"`
while the loader is visible:

```jsx
<section aria-busy={isLoading} aria-label="Properties">
  {isLoading ? <Loader /> : <PropertyList />}
</section>
```

`aria-busy` informs AT that the region's content is in flux; AT
implementations MAY defer announcing live-region updates or content
changes within the busy region.

**Failure pattern.** A Loader without `aria-busy` on the parent region
results in two issues:

- SR doesn't know that the surrounding content is in transition.
- If the loader replaces existing content, SR may announce "page changed"
  inappropriately.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages.

---

## 3. Accessible name

The Loader element MUST carry an accessible name:

| Pattern | Implementation |
|---|---|
| Default | `aria-label="Loading"` (i18n-localised) |
| Contextual | `aria-label="Loading properties"` — when the loader's host region has a specific name, repeat or specialise it |
| Reference | `aria-labelledby="region-title-id"` — when the surrounding region's title id is available |

**Don't use `<title>` inside SVG as the only name source.** SVG `<title>`
is read by some SRs but not consistently; `aria-label` on the outer
element is the canonical approach.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 4. SR-only "Loading…" text

In addition to the visual indicator, the implementation SHOULD include a
visually-hidden text node for fallback:

```jsx
<div role="status" aria-busy="true">
  <Loader variant="spinner" />
  <span class="sr-only">Loading…</span>
</div>
```

The visible Loader carries the sighted-user signal; the SR-only span
carries the AT signal. They are redundant for most SR engines but
defensive: some implementations (especially older or mobile SRs) handle
`role="status"` inconsistently.

**Open question.** Whether `sr-only "Loading…"` should be:

a. EMITTED BY DEFAULT (current contract recommendation).
b. OPT-IN via prop (`announceText?: string`).

Default: (a) — emit by default, allow override via prop. Cost is one
extra DOM node; benefit is universal SR coverage.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages.

---

## 5. Defer timing — prevent flash

Loaders displayed for very brief operations (< 300ms) flash on and off
faster than the user can perceive, creating a visually disruptive
"strobe" without communicating anything useful.

**Recommended pattern.** Defer Loader visibility by 300-500ms:

- If the operation completes in < 300ms, the user never sees the loader.
- If the operation runs longer, the loader appears.

This is an **Interaction-contract concern** (timing of visibility); the
Accessibility contract names the rule because it has accessibility
implications:

- AT users with sluggish SR engines may NEVER hear the live-region
  announcement before the loader disappears.
- Without defer, every fast network response triggers a brief
  `role="status"` announcement that wastes SR cadence.

Implementation pattern:

```jsx
const [showLoader, setShowLoader] = useState(false);
useEffect(() => {
  if (isLoading) {
    const t = setTimeout(() => setShowLoader(true), 300);
    return () => clearTimeout(t);
  } else {
    setShowLoader(false);
  }
}, [isLoading]);
```

**WCAG citations:**
- SC 2.2.2 Pause, Stop, Hide — the loader's animation has a defined
  termination (when loading completes); no user control needed.
- SC 4.1.3 Status Messages — the announcement should be timely AND
  meaningful.

---

## 6. Reduced motion

Per Styling §6: all animations MUST honor `prefers-reduced-motion:
reduce`. The accessibility implication:

- The visual animation channel disappears.
- The `aria-busy` + `aria-label="Loading"` + sr-only "Loading…" text
  channels REMAIN — SR users get full coverage.

For sighted users with reduced-motion preference, the static indicator
(static glyph or "Loading…" text) communicates "in progress" without
animation.

**WCAG citations:**
- SC 2.3.3 Animation from Interactions.

---

## 7. Keyboard

Loader is non-interactive. No keyboard handlers.

If the Loader is composed inside an interactive element (button), that
element's keyboard contract applies (Button.Accessibility §5 covers
the "loading" state behaviour: button remains focusable, click handler
is a no-op).

---

## 8. Focus management

Loader does NOT receive focus. It does NOT move focus on appear or
disappear.

**Critical anti-pattern.** Don't auto-focus the Loader or its container
when loading starts. WCAG 2.2 SC 3.2.1 On Focus forbids unexpected
focus motion. The loader's appearance is announced via the live region;
focus stays where it was.

When the loading completes and content appears, focus also stays put —
the page does not move the user.

**Exception:** If the operation produces a NEW interactive region (e.g.,
a dialog opens after the loader completes), that new region MAY take
focus per its own contract. The Loader itself does not own this
behaviour.

**WCAG citation:** WCAG 2.2 SC 3.2.1 On Focus.

---

## 9. Color contrast

Per [Loader.Styling §2](./Loader.Styling.md) and `feedback.tokens.json`
(proposed) defaults:

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Loader colour on track / surrounding surface (non-text indicator) | 3:1 | SC 1.4.11 |

A spinner is a non-text indicator; the 3:1 contrast rule applies. The
default token (`#2563EB` on `#E5E7EB` light theme) satisfies; provider
overrides MUST re-verify.

**Color is not the only channel.** Loader carries via THREE channels:

1. Visual animation (or static fallback under reduced-motion).
2. `aria-busy="true"` on the containing region.
3. `role="status"` (or `progressbar`) + `aria-label` SR announcement.

**WCAG citation:** WCAG 2.2 SC 1.4.1 Use of Color.

---

## 10. Touch target

Loader is non-interactive — touch-target requirements do NOT apply.

---

## 11. Multiple Loaders on the page

Multiple Loaders may legitimately exist (e.g., different cards each
loading their data independently). The accessibility contract:

- Each Loader has its own `aria-label` to distinguish ("Loading
  properties", "Loading lease summary", etc.).
- Each containing region has its own `aria-busy="true"`.
- SR announcements coalesce — `role="status"` regions are polite, so
  successive announcements queue rather than interrupt.

**Anti-pattern.** Multiple Loaders with identical `aria-label="Loading"`
on the same page. SR users hear "Loading. Loading. Loading." with no
context.

---

## 12. Do / Don't

### Do

- Pair Loader's `role="status"` (or `role="progressbar"` for bar) with
  `aria-busy="true"` on the containing region.
- Provide an `aria-label` that is specific to the context ("Loading
  properties", not just "Loading").
- Include the SR-only "Loading…" span for defensive cross-SR
  compatibility.
- Defer Loader appearance by 300-500ms so brief operations don't flash.
- Honor `prefers-reduced-motion` — keep the SR channel; replace
  visual animation with a static indicator.

### Don't

- Don't ship a Loader without `aria-busy` or `role="status"`. SR users
  get zero signal.
- Don't auto-focus the Loader on appear. Focus stays where it was.
- Don't use Loader for sub-300ms operations.
- Don't use multiple Loaders with identical generic labels on one page.
- Don't substitute `<title>` inside SVG as the only name source —
  inconsistent SR support.

---

## 13. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** any animated SVG or DIV with
  the ARIA wiring above.
- **Web Components (Phase M4, Lit):** simple custom element.

---

## 14. Known gaps — forward-spec validation

| # | Item | Validation criterion |
|---|---|---|
| F1 | `role="status"` (spinner / dots) and `role="progressbar"` (bar) emitted per variant | Snapshot test |
| F2 | `aria-busy="true"` is recommended-pattern emitted on parent region (dev-mode warning if Loader has no aria-busy ancestor) | Dev-mode console-warning test |
| F3 | `aria-label` is required (dev-mode warning if missing) | Dev-mode console-warning test |
| F4 | SR-only "Loading…" span is emitted by default | Snapshot test |
| F5 | 300-500ms defer timing prevents flash on fast operations | E2E test with mocked fast / slow responses |
| F6 | Reduced-motion replaces animation with static indicator; SR channel still works | Manual test with reduce-motion on |
| F7 | Contrast verification — loader colour vs. surrounding surfaces | Run axe scan |
| F8 | Multiple Loaders with distinct labels behave correctly with SR | Manual SR test |
| F9 | Loader inside a Button (loading state) defers to Button.Accessibility §6 | Cross-contract verification |

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #79 Loader (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- [Loader.Semantic.md](./Loader.Semantic.md) — prop contract
- [Loader.Interaction.md](./Loader.Interaction.md) — defer-timing, behavioural contract
- [Loader.Styling.md](./Loader.Styling.md) — token surface + visual states
- [Button.Accessibility.md](../DataEntry/Button.Accessibility.md) §6 — loading state pattern
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `status`, `progressbar`, `aria-busy`, `aria-label`, `aria-valuetext`
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.2.2 Pause, Stop, Hide
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 3.2.1 On Focus
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 4.1.3 Status Messages
