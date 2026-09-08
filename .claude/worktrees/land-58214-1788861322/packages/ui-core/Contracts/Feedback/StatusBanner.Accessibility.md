# StatusBanner — Accessibility Contract

- **Component:** StatusBanner
- **ADR 0017 family:** Feedback
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Interaction](./StatusBanner.Interaction.md) · [Semantic](./StatusBanner.Semantic.md) · [Styling](./StatusBanner.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/StatusBanner.tsx`
- **Catalog row:** not in master catalog — OSS-native component; see catalog appendix §ghost-spec-reconciliation for proposed row
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

## Purpose

Define the accessibility behavior every `StatusBanner` adapter — Blazor, React, and the Phase M4 Web Components track — MUST implement. Expands the accessibility sketch in ADR 0017-A1 §A1.2 into a full contract covering ARIA roles, color contrast, dismiss-button mechanics, icon semantics, screen-reader announcement, and reduced-motion handling. Every requirement is keyed to a WCAG 2.2 AA success criterion.

## Contract

### 1. ARIA roles by `type`

The `type` attribute drives the ARIA live-region category. The mapping is normative.

| `type` | `role` | Live-region politeness | Rationale |
|---|---|---|---|
| `gated` | `alert` | assertive (interrupts current speech) | Action is blocked; user must be informed immediately |
| `warning` | `alert` | assertive (interrupts current speech) | Risk or attention required; cannot wait for idle |
| `provisional` | `status` | polite (queued until idle) | Informational state of an entity; non-urgent |
| `informational` | `status` | polite (queued until idle) | Contextual information; non-urgent |

Both `alert` and `status` are implicit live regions per WAI-ARIA 1.2 — adapters MUST NOT add an additional `aria-live` attribute when the role is set.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages — status messages MUST be programmatically determined through role or properties such that they can be presented to the user by assistive technologies without receiving focus.

### 2. Color contrast

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Message text on `--sf-banner-bg` | 4.5:1 | WCAG 2.2 SC 1.4.3 Contrast (Minimum) |
| Icon glyph on `--sf-banner-bg` (treated as non-text where the icon is decorative; see §4) | 3:1 | WCAG 2.2 SC 1.4.11 Non-text Contrast |
| Border or left-accent stripe on `--sf-banner-bg` | 3:1 | WCAG 2.2 SC 1.4.11 Non-text Contrast |
| Dismiss-button glyph/symbol on its background | 3:1 (graphical), 4.5:1 (text-equivalent) | WCAG 2.2 SC 1.4.11 + SC 1.4.3 |

Concrete token pairs MUST be verified in `_shared/design/tokens/feedback.tokens.json` against the active provider palette for every `type`.

**Color is not the only channel.** Each `type` MUST be distinguishable by at least two channels (color + icon, color + text label, etc.) per WCAG 2.2 SC 1.4.1 Use of Color. The styling contract (`StatusBanner.Styling.md`) requires distinct iconography per `type` for this reason.

### 3. Dismiss button

The dismiss button is rendered only when `dismissible` is true (which defaults per `type` per ADR 0017-A1 §A1.2 — `informational` defaults to `true`; the other three default to `false`).

**Accessible name:** the dismiss button MUST have an accessible name of `"Dismiss"` (or its localized equivalent), supplied via `aria-label="Dismiss"`. The visible affordance MAY be an icon (e.g., an "×" glyph); the icon itself is `aria-hidden="true"` and the name is carried by `aria-label`.

**Keyboard activation:** the dismiss button MUST activate on both `Space` and `Enter`. This is implicit for adapter elements that use a native `<button>`; non-native control implementations (Lit custom element with a non-button host) MUST add this behavior explicitly.

**Escape key dismiss:** when a dismissible banner is visible and keyboard focus is anywhere on the page, pressing `Escape` MUST trigger the same dismiss behavior as activating the dismiss button. This affords keyboard users a fast, predictable exit consistent with common overlay and notification patterns (see also the Interaction contract for Escape handling). Focus-management rules after dismiss apply equally (§3 above, focus-management paragraph).

**WCAG citation:** WCAG 2.1 SC 2.1.1 Keyboard — all functionality MUST be operable through a keyboard interface without requiring specific timings for individual keystrokes.

**Touch target:** the dismiss button MUST present a touch target of at least 24 × 24 CSS pixels. Visual size MAY be smaller if surrounding spacing ensures the effective target stays ≥ 24 × 24 (per WCAG 2.2 SC 2.5.8 spacing exception), but the default styling MUST satisfy the minimum without relying on the exception.

**WCAG citation:** WCAG 2.2 SC 2.5.8 Target Size (Minimum).

**Focus management after dismiss:** when the user activates the dismiss button:

1. The banner transitions to its hidden state (the interaction contract specifies that the component does not unmount — visibility is controlled by the consumer via state).
2. Focus MUST move to either (a) the element that opened or surfaced the banner, if one is identifiable to the adapter (e.g., a trigger button passed by the consumer), or (b) the next logical focus point following the banner in document order. Focus MUST NOT be left on the dismissed banner. Focus MUST NOT be lost (i.e., default to `<body>`).

If the consumer wires focus return explicitly via the `onDismiss` event, that overrides the default — the contract requires only that focus end somewhere meaningful, not that the adapter own the policy beyond the safe default.

### 4. Icon

The per-type icon (`--sf-banner-icon`) is decorative in this context — the type's semantic is conveyed via `role` + message text + (for screen-reader users) the live-region announcement. The icon MUST therefore be marked `aria-hidden="true"` so it is not announced redundantly.

For adapters that render the icon as an inline SVG, `aria-hidden="true"` on the `<svg>` element is sufficient. For adapters that render the icon via a CSS background-image or `::before` pseudo-element, the icon is already non-content to assistive technology — no ARIA marker is required, but adapters SHOULD ensure the pseudo-element does not introduce announceable text.

### 5. Screen-reader announcement

The banner is announced by assistive technology under the following conditions:

1. **On insertion into the DOM** — when a banner mounts (e.g., the consumer flips state and the banner enters the tree), the live region fires per the politeness of its `role` (`alert` interrupts; `status` queues).
2. **On `visible` state change from hidden → visible** — adapters that toggle visibility via `hidden` attribute / `display: none` rather than DOM insertion MUST ensure the live region re-fires on the show transition. The recommended technique is to clear the live-region's child content and re-set it on the visibility flip; specific mechanics are an adapter implementation concern.
3. **On message-content change while visible** — when the consumer updates the message text on a mounted banner, the live region announces the new content per its politeness level.

The banner MUST NOT steal focus on mount. The live-region mechanism is the announcement channel; focus-stealing would violate WCAG 2.2 SC 3.2.1 On Focus.

### 6. Reduced motion

If the banner has any enter, exit, or visibility-toggle animation (fade, slide, scale, etc.), that animation MUST be disabled or reduced when the user has requested reduced motion via `prefers-reduced-motion: reduce`.

Acceptable strategies:

- Disable the transition entirely (instant show/hide).
- Reduce the transition duration to ≤ 100 ms with no transform.
- Use a cross-fade only (no movement).

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions — motion animation triggered by interaction can be disabled, unless the animation is essential to the functionality. Banner show/hide animation is decorative; it MUST honor the user's preference.

### 7. Language and localization

The accessible name on the dismiss button (`"Dismiss"`) is text content and MUST be supplied via the localization layer, not hard-coded. The contract requires only that the accessible name exists and is appropriate for the current locale.

The banner's `lang` attribute follows the document's `lang` per WCAG 2.2 SC 3.1.1 Language of Page. Adapters do not need to set `lang` explicitly unless the banner content is in a language different from the surrounding document — in which case the consumer (not the banner) supplies the `lang` attribute on the slotted content.

## Do / Don't

### Do

- Pick the `role` from the table in §1 by `type` — never override it based on adapter convention.
- Verify every `--sf-banner-bg` / `--sf-banner-fg` pair in `feedback.tokens.json` meets 4.5:1 (text) and 3:1 (icon/border) before merging.
- Render the dismiss button as a native `<button>` where the host language supports it (Blazor's `<button>` tag, React's `<button>` JSX, native button mode in Lit) to inherit Space/Enter activation, focus styling, and AT exposure.
- Support Escape key dismiss (page-level keyboard handler) for dismissible banners — required by WCAG 2.1 SC 2.1.1.
- Mark per-type icons `aria-hidden="true"`; rely on `role` + message text for semantic conveyance.
- Return focus to a meaningful point after dismiss; the adapter's default is the trigger or the next focusable element, and consumers may override via `onDismiss`.
- Honor `prefers-reduced-motion: reduce` for any banner animation.

### Don't

- Don't add `aria-live` to the host element when `role="alert"` or `role="status"` is set — the live region is implicit.
- Don't use `role="alertdialog"` — the banner is not modal and does not contain interactive controls beyond an optional dismiss button.
- Don't focus the banner on mount (focus-stealing). Use the live region for announcement.
- Don't rely on color alone to distinguish `type` — pair color with icon (and ideally a visible text label or per-type leading word in the message).
- Don't omit the accessible name on the dismiss button when the button glyph is non-text.
- Don't allow the dismiss button to fall below 24 × 24 CSS pixels.
- Don't let focus disappear after dismiss (focus on `<body>` is failure).

## Parity notes

- **Blazor:** dismiss button is a `<button @onclick="HandleDismiss">` — Space/Enter activation, focus ring, and AT exposure are inherited.
- **React:** dismiss button is a `<button onClick={handleDismiss}>` — same inheritance. `aria-label` provided via JSX prop.
- **Web Components (Phase M4, Lit):** dismiss button is an internal `<button>` inside the shadow tree; `aria-label` set via Lit template. Live region content lives in the light DOM or in the shadow tree with `slot=""` exposure as the WC track decides (deferred to M4 design).

## Open questions

- Whether the banner SHOULD support a focusable "skip to dismiss" affordance when the message contains a long block of text (i.e., a `tabindex="0"` heading-equivalent inside the banner). Current contract: no; the dismiss button is the only focusable affordance inside the banner. Revisit when usage data surfaces long-message banners.
- Whether the `onDismiss` event payload SHOULD carry a hint about the focus-return target (so consumers can override without writing custom focus code). Current contract: no payload — consumers wire focus return themselves via the event handler. Revisit at Phase M2 (React adapter) when more usage patterns surface.

## References

- ADR 0017 §A1.2 — StatusBanner accessibility sketch (this contract expands)
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `alert` role, `status` role, live regions
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.1 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
- WCAG 2.2 SC 3.1.1 Language of Page
- WCAG 2.2 SC 3.2.1 On Focus
- WCAG 2.2 SC 4.1.3 Status Messages
