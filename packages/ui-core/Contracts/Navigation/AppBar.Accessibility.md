# AppBar — Accessibility Contract

- **Component:** AppBar
- **ADR 0017 family:** Navigation
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./AppBar.Semantic.md) · [Interaction](./AppBar.Interaction.md) · [Styling](./AppBar.Styling.md)
- **Reference implementation:** _none yet — forward-spec; foundation per Vaadin AppLayout app-bar slot_
- **Catalog row:** #4 AppBar (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2

---

## 1. Purpose

AppBar carries the application's primary banner landmark. Its accessibility
responsibilities centre on landmark wiring, the skip-nav requirement, and
child-element name composition.

This contract pins:

1. The landmark `role="banner"` (via `<header>` element with appropriate scope).
2. The required skip-navigation link as the first focusable child.
3. Brand-mark accessible-name rules (image + text wordmarks).
4. The page-title `aria-current="page"` pattern.
5. The boundary against child-element responsibilities (SideNav toggle,
   identity menu, search input — each owns its own contract).

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. Root element + landmark

| Choice | Recommendation |
|---|---|
| Underlying element | Native `<header>` |
| Default ARIA role | implicit `banner` from `<header>` placed as a direct child of `<body>` (or top-level `<main>` ancestor). DO NOT add `role="banner"` redundantly when `<header>` is at top-level |
| Explicit `role="banner"` | REQUIRED when `<header>` is NOT a direct child of `<body>` (e.g., inside a containing wrapper that breaks the implicit-landmark heuristic) |
| `aria-label` on the banner | OPTIONAL but recommended when the page contains multiple `<header>` elements (rare) — distinguishes "main application header" from section headers |
| `tabindex` | NEVER on AppBar itself |

**Landmark uniqueness.** There MUST be exactly ONE `role="banner"` per page.
A page with multiple AppBar instances violates the landmark uniqueness rule.

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships; WCAG 2.2 SC 2.4.1
Bypass Blocks (the banner landmark is one of the bypass mechanisms — see §3).

---

## 3. Skip navigation

A skip-navigation link MUST be available so keyboard users can bypass the
AppBar (and any other landmarks above the main content) and jump directly to
the main content region.

| Element | Required emission |
|---|---|
| Skip link | First focusable child inside AppBar (or as the first focusable element on the page if AppBar's `start` slot does not yet contain it) |
| Default text | "Skip to main content" (i18n-localised) |
| Target | `<a href="#main">` referencing the page's `<main id="main">` element |
| Visibility | Visually hidden by default (`sr-only` class); becomes visible when focused (`focus:not-sr-only`) — see Styling §3.4 for the recipe |

**WCAG citations:**
- SC 2.4.1 Bypass Blocks — the skip link IS the bypass mechanism.
- SC 2.4.7 Focus Visible — the link MUST become visible when focused.

**Forbidden patterns:**

- Skip link with `display: none` (it's not focusable, so it provides no benefit).
- Skip link target (`#main`) that doesn't exist on the page (the link goes
  nowhere). Consumer responsibility; AppBar SHOULD warn in dev-mode when
  the target is missing.
- Multiple skip links inside AppBar (one is sufficient; redundancy slows SR
  users).

---

## 4. Brand mark

The brand mark is typically a logo (image or icon) + the application's
wordmark. Its accessible-name handling:

| Shape | Implementation |
|---|---|
| Image-only logo: `<img src="logo.svg" alt="Acme">` | `alt` attribute IS the accessible name |
| Inline-SVG logo: `<svg role="img" aria-label="Acme"><title>Acme</title>...</svg>` | `aria-label` OR `<title>` element IS the accessible name |
| Wordmark text only: `<span>Acme</span>` | Visible text IS the accessible name |
| Logo + wordmark: `<a href="/"><LogoIcon aria-hidden="true" /><span>Acme</span></a>` | Wordmark text IS the accessible name; logo is decorated as hidden so SR doesn't read it twice |

**Brand-mark link.** When the brand mark navigates (typically to `/` or
home), wrap in `<a href="...">`; the link's accessible name is "Acme" (or
the equivalent). SR users hear "Acme, link" — clear and concise.

**Forbidden patterns:**

- Image logo with `alt=""` → invisible to SR.
- Image logo with `alt="logo"` → uninformative.
- Logo + wordmark BOTH announced (e.g., logo's alt="Acme" + visible wordmark
  "Acme") → "Acme Acme" double-speak; decorate one of the two as hidden.

**WCAG citation:** WCAG 2.2 SC 1.1.1 Non-text Content.

---

## 5. Page title region (center slot)

The centre slot typically contains the current page title. The title MAY
be marked with `aria-current="page"` if it doubles as the link to the
current page (e.g., breadcrumb or active-nav-item pattern). Otherwise, no
ARIA emissions are required — the heading hierarchy of the page (`<h1>`
inside `<main>`) carries the document outline; the AppBar's title region
is supplementary.

**Important rule.** The AppBar's centre slot is NOT the page's `<h1>`. The
`<h1>` lives inside `<main>`. The AppBar title is presentational. Pinning
the centre slot as `<h1>` creates a heading-outline anomaly (the heading
appears OUTSIDE the main content region).

---

## 6. Keyboard

AppBar itself binds no keyboard handlers. Keyboard traversal of AppBar's
children:

| Key | Behaviour | Where it's handled |
|---|---|---|
| Tab | First lands on the skip-nav link; then traverses start → center → end children in DOM order | Browser default |
| Shift+Tab | Reverses traversal | Browser default |
| Enter / Space | Activates whichever focused child supports it | Each child (button, link, etc.) |
| Arrow keys | NOT bound at AppBar level. Inline-nav children (if any) MAY bind arrows for roving-tabindex within their own scope | Inline-nav child |

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard.

---

## 7. Focus management

AppBar does NOT manage focus. Each child element (brand link, menu toggle,
identity menu) carries its own focus contract.

When a SR or keyboard user navigates the page and the skip-nav link is the
first focusable element, the user's Tab keypress lands on the skip-nav
first — bypassing the rest of AppBar's chrome — and Enter sends focus to
the `#main` target. This is the prescribed bypass pathway.

**WCAG citation:** WCAG 2.2 SC 2.4.7 Focus Visible (each focusable child
emits its own focus ring per its own contract); SC 2.4.11 Focus Not
Obscured (the AppBar MUST NOT visually cover a focused element below it —
sticky positioning + focus-scroll combinations need testing).

---

## 8. Sticky positioning + focus visibility

When AppBar uses `position: sticky; top: 0;` (the common pattern), focused
elements lower in the page MAY scroll partially under the AppBar. WCAG 2.2
SC 2.4.11 Focus Not Obscured (Minimum) REQUIRES that the focused element
NOT be entirely hidden by author-positioned overlays — partial occlusion
is acceptable at AA.

**Implementation responsibility.** The consumer's stylesheet uses
`scroll-margin-top: var(--sf-appbar-height)` on `<main>` or on focusable
elements to ensure the browser scrolls focused elements clear of the
AppBar. AppBar SHOULD provide a CSS custom property (`--sf-appbar-height`)
that consumers reference; the token surface (§2.1) already exposes this.

**Forward-spec validation.** The implementation MUST be tested with sticky
AppBar + Tab traversal of a long page; focused elements should remain at
least partially visible.

**WCAG citation:** WCAG 2.2 SC 2.4.11 Focus Not Obscured (Minimum).

---

## 9. Mobile-drawer trigger

When AppBar's `start` slot contains a "hamburger" toggle that opens a
SideNav drawer on mobile, the toggle MUST satisfy:

| Requirement | Emission |
|---|---|
| Role | Native `<button>` |
| Accessible name | `aria-label="Open navigation"` (closed state) / `aria-label="Close navigation"` (open state) |
| State | `aria-expanded="false"` (closed) / `aria-expanded="true"` (open) |
| Controls | `aria-controls="<sideNavId>"` pointing to the SideNav region |

The toggle's contract is owned by the SideNav Accessibility contract (it
contains the canonical wording); AppBar's accessibility contract references
the SideNav contract but does not duplicate it.

---

## 10. Color contrast

Per [AppBar.Styling §2](./AppBar.Styling.md) and the proposed
`navigation.tokens.json` defaults:

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| AppBar foreground (`--sf-appbar-fg`) on AppBar background (`--sf-appbar-bg`) | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Brand wordmark on AppBar background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Border-bottom on AppBar background | 3:1 | WCAG 2.2 SC 1.4.11 |
| Skip-link foreground on skip-link background (when focused) | 4.5:1 | WCAG 2.2 SC 1.4.3 |

Provider overrides (branded backgrounds, etc.) MUST re-verify.

---

## 11. Reduced motion

AppBar has NO animations by default. Optional scroll-aware elevation
transition (Styling §7 Q1) MUST honor `prefers-reduced-motion: reduce`
when implemented.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 12. Do / Don't

### Do

- Render AppBar as native `<header>` placed at top-level (direct child of
  `<body>` or `<main>` ancestor).
- Include skip-nav as the first focusable child.
- Decorate logo icons as `aria-hidden="true"` when the wordmark text is
  also present.
- Provide `aria-label` on the mobile-drawer toggle that reflects the
  current state.
- Use `scroll-margin-top: var(--sf-appbar-height)` on `<main>` to keep
  focused elements visible past sticky AppBar.

### Don't

- Don't ship more than ONE `role="banner"` landmark per page.
- Don't ship AppBar without the skip-nav link.
- Don't pin the AppBar centre-slot title as `<h1>`. The `<h1>` lives
  inside `<main>`.
- Don't `display: none` the skip-nav link. Use `sr-only` / `focus:not-sr-only`.
- Don't use colour alone to signal sticky-state — let shadow + border do
  the work.

---

## 13. Parity notes

- **Blazor (M4 reverse-spec):** TBD.
- **React (this contract, forward-spec):** `<header>` + skip-nav + three
  slot subcomponents.
- **Web Components (Phase M4, Lit):** Vaadin's `<vaadin-app-layout>` is
  the primary reference.

---

## 14. Known gaps — forward-spec validation

| # | Item | Validation criterion |
|---|---|---|
| F1 | Implicit `role="banner"` is emitted by `<header>` at top-level (no redundant explicit role) | Snapshot test |
| F2 | Skip-nav link is the first focusable child AND visually hidden until focused | Manual Tab traversal test; visual regression |
| F3 | Skip-nav target `#main` exists on the host page (dev-mode warning when missing) | Dev-mode console-warning test |
| F4 | Brand-mark logo + wordmark composition decorates logo as `aria-hidden` | Snapshot test |
| F5 | `scroll-margin-top` is set on `<main>` so focused elements clear sticky AppBar | E2E test: focus an element below the fold; verify visible |
| F6 | Mobile-drawer toggle emits correct `aria-expanded` / `aria-controls` (verified in concert with SideNav contract) | Snapshot + manual SR test |
| F7 | Contrast verification for AppBar fg/bg + skip-link fg/bg + border | Run axe scan |
| F8 | Centre-slot title truncates with ellipsis when narrow | Visual regression at multiple viewport widths |

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #4 AppBar (high, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- Vaadin AppLayout — reference foundation
- [AppBar.Semantic.md](./AppBar.Semantic.md) — prop contract
- [AppBar.Interaction.md](./AppBar.Interaction.md) — behavioural contract
- [AppBar.Styling.md](./AppBar.Styling.md) — token surface + visual states
- [SideNav.Accessibility.md](./SideNav.Accessibility.md) — mobile-drawer toggle contract
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `banner`, `aria-label`, `aria-current`, `aria-expanded`, `aria-controls`
- WCAG 2.2 SC 1.1.1 Non-text Content
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.1 Bypass Blocks
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.4.11 Focus Not Obscured (Minimum)
