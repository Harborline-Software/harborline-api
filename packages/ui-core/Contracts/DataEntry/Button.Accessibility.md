# Button — Accessibility Contract

- **Component:** Button
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Button.Semantic.md) · [Interaction](./Button.Interaction.md) · [Styling](./Button.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/Button.tsx` (foundation: Radix Slot + native `<button>`)
- **Catalog row:** #17 Button (`app-priority: critical`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

Button is the most ubiquitous interactive primitive in the system; its
accessibility contract is consequently the most consequential. This
contract pins:

1. The ARIA + native-element baseline (`<button>` + `aria-label` / `aria-
   pressed` / `aria-disabled` / `aria-busy`).
2. The keyboard model (Space + Enter activation; Tab / Shift+Tab focus
   traversal; no arrow-key navigation at the Button level).
3. The focus-visible requirement (focus ring on keyboard focus only).
4. The cross-channel signal rule for destructive variant.
5. The touch-target rule (24 × 24 minimum per WCAG 2.2 SC 2.5.8) and the
   Spacing Exception that permits the `sm` size in dense toolbars.

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice. Forward-spec means there is no shipping baseline to
document; instead this contract names what the implementation MUST emit.

---

## 2. Root element + role

| Choice | Recommendation |
|---|---|
| Underlying element | Native `<button>` (or `<a>` for "looks like button, navigates like link" — see §10) |
| Default `type` attribute | `type="button"` (NOT the HTML form-default `type="submit"`) unless explicitly used as a form submit button |
| Default ARIA role | implicit `button` from `<button>` — DO NOT add `role="button"` redundantly |
| When `asChild` (Radix Slot pattern) | The composed element MUST be a valid interactive element; if it is NOT `<button>` or `<a>`, the slot consumer is responsible for adding `role="button"` + `tabindex="0"` + Space/Enter handlers. The component contract REQUIRES the consumer accept this responsibility when overriding. |

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value. The native `<button>`
element satisfies role automatically; `<div>` masquerading as button requires
all four characteristics (name, role, value, state) to be added by hand.

**Open question.** Whether to ban `asChild` rendering of `<div>` outright,
or to permit it with the caveat above. Default: permit but require the
consumer carry the burden; council confirms.

---

## 3. Accessible name

Every Button MUST expose an accessible name to AT. The name is computed
per ARIA 1.2's accessible name calculation:

| Source | Priority | When |
|---|---|---|
| `aria-labelledby` | 1 (highest) | If supplied, wins over all other sources |
| `aria-label` | 2 | Required when the button has NO visible text (icon-only) |
| Visible text content | 3 | Default for text buttons; visible label IS the accessible name |
| `title` attribute | 4 (fallback only) | NOT a substitute for `aria-label`; SR support is inconsistent |

| Button shape | Name source |
|---|---|
| Text button: `<Button>Save</Button>` | "Save" (visible text) |
| Icon + text: `<Button><SaveIcon /> Save</Button>` | "Save" (visible text; icon decorated `aria-hidden="true"`) |
| Icon-only: `<Button aria-label="Save"><SaveIcon /></Button>` | "Save" (from `aria-label`) — REQUIRED on icon-only |
| `asChild` with `<a>` containing icon: `<Button asChild><a href="/profile" aria-label="Profile"><UserIcon /></a></Button>` | "Profile" (from `aria-label` on the rendered `<a>`) |

**Forbidden patterns:**

- Icon-only button WITHOUT `aria-label` → AT announces nothing or the icon
  filename. WCAG 2.2 SC 4.1.2 violation.
- `title` attribute as the only name source on icon-only buttons → inconsistent
  SR support; relegate to supplementary use.
- `aria-label` that duplicates visible text → "double speaking" in SR. Omit
  `aria-label` when visible text exists.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 4. Variant-specific accessibility requirements

### 4.1 Destructive variant: cross-channel signal

The `destructive` variant uses the danger-red colour family. WCAG 2.2 SC
1.4.1 Use of Color forbids colour as the sole channel for meaning.

**REQUIREMENT.** When `variant="destructive"`, the button's label text
MUST contain a destructive verb. The component SHOULD warn (dev-mode
console warning) when this is not satisfied; council confirms whether
the warning rises to a runtime invariant.

| Variant | Required label semantics | Allowed labels |
|---|---|---|
| `destructive` | Destructive verb | "Delete", "Remove", "Discard", "Drop", "Destroy", "Reset", "Clear all", "Revoke" |
| `destructive` | NOT allowed | "OK", "Yes", "Continue" (these provide colour-only destruction signal) |

Icon-only destructive buttons MUST still satisfy this via `aria-label`
("Delete row" — not "Delete-icon").

**WCAG citation:** WCAG 2.2 SC 1.4.1 Use of Color.

### 4.2 Primary variant: count discipline

Not strictly an accessibility requirement, but documented here because the
implication is cognitive: multiple `primary` buttons on one surface dilute
the visual hierarchy and force the user (especially low-vision users) to
read every label to decide. The Styling contract names this as the
recommended discipline; the Semantic contract MAY add a runtime warning.

---

## 5. Keyboard

| Key | Behaviour | WCAG citation |
|---|---|---|
| Tab | Move focus IN (forward order); MUST land on the button when reached | SC 2.1.1 |
| Shift+Tab | Move focus OUT (reverse order) | SC 2.1.1 |
| Enter | Activate (`click` event fires) | SC 2.1.1 — inherited from native `<button>` |
| Space | Activate (`click` event fires on key-up, not key-down) | SC 2.1.1 — inherited |
| Escape | NOT bound by Button itself; the containing dialog/popover may bind it to dismiss | n/a |
| Arrow keys | NOT bound at the Button level. A `ButtonGroup` / `ToolBar` / `Tabs` parent MAY bind arrows for roving-tabindex within its scope | n/a |

**Loading state.** A button in `loading` state MUST remain keyboard-focusable
and SHOULD NOT swallow Enter/Space silently. Instead, the implementation
either:

a. Allows activation but the click handler is a no-op (the spinner is the
   feedback that an action is in flight); OR
b. Blocks activation and announces "action in progress" via the `aria-busy`
   live region (see §6).

Pick (a) for simplicity; (b) if the action surface has a longer-running
operation and the SR user benefits from explicit "wait" feedback. Default:
(a).

**Disabled state.** A truly `disabled` button (`disabled` attribute) is
removed from the tab order AND cannot be activated. An `aria-disabled="true"`
button (the focusable-disabled variant) remains in tab order, MUST visually
appear disabled, and MUST NOT execute its click handler. See §7.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard.

---

## 6. Loading state

The `loading` prop indicates an asynchronous action is in flight.
Accessibility emissions during loading:

| Channel | Emission | Notes |
|---|---|---|
| `aria-busy` | `"true"` on the button OR on a wrapping region | Tells AT the control is currently busy; SR may announce "busy" |
| `aria-disabled` | NOT set during loading | The button stays focusable; activation is gated by handler-no-op per §5 |
| Spinner icon | `aria-hidden="true"` on the spinner SVG | Spinner is purely visual; the `aria-busy` channel carries the SR signal |
| Label text | UNCHANGED during loading | Width stays stable; SR re-reads the same label on focus |
| Live region | Optional `<span class="sr-only" aria-live="polite">{loadingMessage}</span>` for long-running operations | Used only when the action is expected to take > 2s |

**Reduced motion.** The spinner animation MUST honor
`prefers-reduced-motion: reduce` per Styling §6 — replace continuous
rotation with a static "loading" glyph or slow pulse.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages.

---

## 7. Disabled state — two variants

Two semantically distinct disabled patterns are supported.

### 7.1 Native `disabled` (default)

```jsx
<Button disabled>Save</Button>
```

- Emits HTML `disabled` attribute on `<button>`.
- Removed from tab order.
- Cannot be activated by Enter / Space / click.
- AT announces "Save, button, dimmed" (or equivalent).
- Use when the button is unavailable AND the user does not need to know
  why.

### 7.2 Focusable `aria-disabled` (intentional)

```jsx
<Button aria-disabled="true">Save</Button>
```

- Emits `aria-disabled="true"`; NO native `disabled` attribute.
- Remains in tab order.
- Click handler is gated by the component (no-op when `aria-disabled`).
- AT announces "Save, button, dimmed" but the user CAN focus and see the
  button.
- Use when the user needs to know the button exists and tab to it to
  receive contextual help (e.g., a tooltip explaining what's missing).
- The component contract requires the consumer also supply
  `aria-describedby` pointing to the explanation when using this variant
  — otherwise the user receives no benefit from the focusable disabled
  pattern.

**Choice rule.** Default to `disabled` (7.1). Reach for `aria-disabled`
(7.2) only when the disabled-reason context is load-bearing for the user
(e.g., a form's "Submit" disabled until all required fields are valid —
the user benefits from tabbing to it to discover what's missing).

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships (the
`aria-disabled` + `aria-describedby` pair makes the disabled-reason
programmatically determinable).

---

## 8. Focus management + focus-visible

### 8.1 Focus ring requirement

Every Button MUST display a visible focus indicator when focused via
keyboard. The default implementation uses CSS `:focus-visible` so pointer-
focus does NOT trigger the ring (matches platform convention).

The ring tokens are defined in Styling §2.3:
`--sf-btn-focus-ring-color` / `-width` / `-offset`.

**WCAG citations:**
- SC 2.4.7 Focus Visible — any keyboard-operable interface MUST have a
  visible focus indicator.
- SC 2.4.11 Focus Not Obscured (Minimum) — the focus indicator MUST NOT
  be hidden by author-provided overlays.
- SC 2.4.13 Focus Appearance — the focus ring MUST be at least 2px and
  meet 3:1 contrast against both the button and the surrounding surface.

### 8.2 Focus return after activation

Button does NOT manage focus on activation; the click handler may navigate
the user elsewhere, in which case the activating button is unmounted and
focus management becomes the receiving surface's concern.

For in-place activations (e.g., a toggle button), focus stays on the
button — native browser behaviour, no special handling required.

For destructive activations that open a confirmation dialog: the dialog
MUST capture initial focus (the Dialog Accessibility contract owns this);
on dialog close, focus MUST return to the activating button (also Dialog's
concern).

**WCAG citation:** SC 3.2.2 On Input — activation MUST NOT cause an
unexpected context change without warning.

---

## 9. Touch target

Per WCAG 2.2 SC 2.5.8 Target Size (Minimum), interactive targets MUST
present at least 24 × 24 CSS pixels — with two exceptions:

a. **Inline exception.** Targets in flowing text are exempt.
b. **Spacing exception.** Targets with at least 24 × 24 spacing between
   their centres and adjacent target centres are exempt.
c. **User agent control.** Targets controlled by the UA (browser default
   form controls) are exempt.

Button's size matrix:

| Size | Min-height | Min-width (text button) | Compliant? |
|---|---|---|---|
| `sm` | 32px | dependent on label | NO — falls below 44×44 best-practice; relies on Spacing Exception when used in dense toolbars |
| `md` | 40px | dependent on label | NO at 24×24 strict; YES under common spacing |
| `lg` | 44px | dependent on label | YES |

**Rule.** Default to `md` for standalone buttons. Use `sm` only inside
toolbars/groups where adjacent button centres are at least 24px apart —
the Spacing Exception applies.

**Icon-only buttons.** MUST be square at every size to preserve the target
area. `sm` icon-only = 32×32; `md` icon-only = 40×40; `lg` icon-only = 44×44.

**WCAG citation:** WCAG 2.2 SC 2.5.8 Target Size (Minimum).

---

## 10. When NOT to use Button — link semantics

Native HTML draws a sharp line:

| Action | Element |
|---|---|
| Triggers an in-page action (submit form, open dialog, toggle state) | `<button>` |
| Navigates to a URL | `<a href="...">` |

A "button" that looks like a button but navigates MUST be an `<a>` with
button styling (via `<Button asChild><a>...</a></Button>` — Radix Slot
pattern). Reasons:

- Screen readers announce "link" vs. "button" — the user expects
  navigation from "link" and in-page action from "button".
- Browsers expose link-specific affordances (right-click "Open in new
  tab", Cmd+click to open in new tab, copy link address) that button
  semantics suppress.
- `<button>` activated with Enter triggers form submission if inside a
  `<form>` and `type` is not specified; `<a>` does not.

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value — the role
programmatically informs the user of the expected behaviour.

---

## 11. Color contrast

Per [Button.Styling §4](./Button.Styling.md) state inventory and
`forms.tokens.json` defaults:

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Label text on button background (every variant × every state) | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Button border on background (where border carries shape) | 3:1 | WCAG 2.2 SC 1.4.11 |
| Focus ring on BOTH the button surface AND the surrounding surface | 3:1 | WCAG 2.2 SC 1.4.11 / SC 2.4.13 |
| Disabled label on disabled background | NOT subject to 4.5:1 per WCAG (disabled controls exempted by SC 1.4.3 Note 5) | informational; provide visual disabledness via opacity |

The default token values in `forms.tokens.json` (proposed) MUST be
pre-verified. Provider overrides MUST re-verify.

**Color is not the only channel.** Disabled state is signaled by opacity +
cursor + aria-disabled (not colour alone). Destructive is signaled by
colour + label verb (not colour alone). Focus is signaled by ring +
optional darkening (not colour alone — the ring shape carries the signal).

**WCAG citation:** WCAG 2.2 SC 1.4.1 Use of Color.

---

## 12. Reduced motion

Per Styling §6: the hover transition and the loading spinner MUST honor
`prefers-reduced-motion: reduce`.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 13. Do / Don't

### Do

- Use native `<button>` as the rendered element unless `asChild` substitutes
  another semantic primitive (`<a>` for navigation).
- Provide `aria-label` on every icon-only button. NEVER ship icon-only
  without an accessible name.
- Pair destructive variant with destructive verb in the label.
- Use `focus-visible:` (NOT `focus:`) so pointer-focus does not show the ring.
- Default to `disabled` for unavailable buttons; reach for `aria-disabled`
  only when the focusable-disabled pattern adds value.
- Default to `md` size for standalone buttons; reserve `sm` for dense
  toolbars where Spacing Exception applies.
- Honor `prefers-reduced-motion` on hover transition and loading spinner.

### Don't

- Don't ship a button-styled `<div>` without `role="button"` + tabindex +
  Space/Enter handlers (and don't choose `<div>` if `<button>` works).
- Don't use colour as the only signal for destructive (verb required) or
  disabled (opacity + cursor + aria required).
- Don't drop `aria-label` from icon-only buttons because the icon "looks
  obvious" — SR users don't see the icon.
- Don't use `title` as the only accessible name source on icon-only.
- Don't use `aria-disabled` without also gating the click handler.
- Don't use `aria-disabled` without `aria-describedby` pointing to the
  reason — otherwise the user sees a disabled button with no path to
  unblock.
- Don't drop below 32px min-height. The Spacing Exception is a
  conditional; if buttons are <24px apart, you've broken WCAG.
- Don't animate the focus-ring appearance (`transition-shadow` or
  `transition-all`) — the ring must appear instantly when focus lands.

---

## 14. Parity notes

- **Blazor (M4 reverse-spec):** TBD; pattern likely uses `<button>` with
  class-based variant resolution and Blazor's `KeyboardEventArgs` for
  Space/Enter handling.
- **React (this contract, M1 shipping):** shadcn Button + Radix Slot for
  `asChild` composition; className-based variant resolution; native
  `<button>` element handles Space/Enter activation.
- **Web Components (Phase M4, Lit):** TBD; the WC track adopts whichever
  baseline is canonical at M4 entry. ARIA emissions are framework-neutral
  and carry forward unchanged.

---

## 15. Known gaps — M1 shipping audit

Per the DA3-11 reconciliation sweep, the M1 shipping implementation
covers the structural ARIA surface (native `<button>`, `disabled` attribute,
className-driven variant). The following items still require verification
against the live implementation:

| # | Item | Validation criterion | M1 status |
|---|---|---|---|
| F1 | `asChild` rendering paths preserve `<button>` semantics | Render `<Button asChild><a>...</a></Button>` and verify SR announces "link" + correct accessible name | Implemented via Radix `Slot`; SR validation outstanding |
| F2 | `loading` state retains keyboard focusability | Tab to a loading button; verify focus ring appears and Enter/Space are no-ops (per §5) | Implementation sets `disabled={disabled || loading}` — focusability under `disabled` is browser-default-false; revisit if focusable-disabled is needed |
| F3 | `aria-disabled` focusable-disabled pattern works in concert with `aria-describedby` | E2E test: tab to aria-disabled button, verify SR reads label + description | NOT implemented (uses native `disabled`); upgrade-path tracked under G-BAC1 |
| F4 | `focus-visible` is honored across all supported browsers | Manual test in Chrome, Firefox, Safari (mobile + desktop) | Implementation uses Tailwind `focus-visible:ring-*` utilities; cross-browser validation outstanding |
| F5 | Destructive verb invariant (dev-mode warning) does not fire false-positives in non-English locales | Test with i18n labels in fr-FR ("Supprimer"), de-DE ("Löschen") | NOT implemented (no dev-mode warning ships in M1); revisit under PAO Accessibility runtime-check follow-up |
| F6 | Touch-target minimum (24×24) verified for icon-only at every size | Run axe scan + manual measurement | Implementation `size="icon"` ships `h-10 w-10` (40×40); meets WCAG 2.5.8 minimum |
| F7 | `prefers-reduced-motion` correctly suppresses both transition AND spinner | Manual test with macOS Reduce Motion on; verify no animation on hover or during loading | NOT implemented at Button level; relies on global CSS reduced-motion media query |
| F8 | Form-default `type="submit"` is NOT inherited when Button is composed inside a `<form>` | Render Button inside `<form>`; verify Enter inside a TextField does NOT trigger the Button unless `type="submit"` is explicit | Implementation defaults `type="button"` explicitly; validated by reading source |

### M1 vs spec deltas — known gaps log

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-BAC1 | Medium | M1 implementation uses native `disabled` rather than the `aria-disabled` focusable-disabled pattern called out in this contract | [ACCEPTED-RISK 2026-06-06] Native `disabled` is the simpler default; focusable-disabled is a follow-up enhancement if users report screen-reader navigation gaps |
| G-BAC2 | Low | M1 implementation does not ship destructive-verb dev-mode warning (F5) nor icon-button `aria-label` dev-mode warning | [ACCEPTED-RISK 2026-06-06] Contract-only conventions for M1; PAO Accessibility may add runtime checks in a follow-up amendment |
| G-BAC3 | Low | M1 implementation does not ship explicit `prefers-reduced-motion` handling at Button level (F7) | [ACCEPTED-RISK 2026-06-06] Tailwind `transition-colors` honors the global `motion-reduce:` cascade via the design-token layer |

---

## References

- ADR 0017 §A1.3 — component family scope
- Catalog #17 Button (critical, v1) — `packages/ui-core/Contracts/component-master-catalog.md`
- shadcn Button — Radix Slot + native button (foundation)
- [Button.Semantic.md](./Button.Semantic.md) — prop contract
- [Button.Interaction.md](./Button.Interaction.md) — behavioural contract
- [Button.Styling.md](./Button.Styling.md) — token surface + visual states
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `button`, `aria-label`, `aria-labelledby`, `aria-disabled`, `aria-busy`, `aria-pressed`
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.4.11 Focus Not Obscured (Minimum)
- WCAG 2.2 SC 2.4.13 Focus Appearance
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
- WCAG 2.2 SC 3.2.2 On Input
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 4.1.3 Status Messages
