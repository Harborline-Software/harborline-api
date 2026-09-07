# EmptyState — Accessibility Contract

- **Component:** EmptyState
- **ADR 0017 family:** DataDisplay
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./EmptyState.Semantic.md) · [Interaction](./EmptyState.Interaction.md) · [Styling](./EmptyState.Styling.md)
- **Related contract:** [DataGrid.Accessibility.md](./DataGrid.Accessibility.md) — DataGrid's `emptyState` slot accepts an EmptyState node; this contract names the live-region expectation when composing.
- **Reference implementation:** `packages/ui-react/src/components/datagrid/EmptyState.tsx`
- **Catalog row:** A18 EmptyState (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

EmptyState is a small presentational component: an icon + title + optional
description + optional CTA. Its accessibility surface covers four concerns:

1. The icon is decorative — it MUST be hidden from AT to avoid duplicate
   announcement of the variant's meaning (which is carried by the title text).
2. The title text MUST be programmatically determinable so AT reads it as
   the primary message.
3. The CTA, when present, MUST be keyboard-activatable and focusable like any
   native button.
4. When EmptyState composes inside a DataGrid that transitions from
   populated → empty, the transition SHOULD be announced via a polite live
   region. The M1 implementation does NOT carry an inherent live region; this
   contract names the gap.

Every requirement is keyed to a WCAG 2.2 AA success criterion or a WAI-ARIA
1.2 authoring practice.

---

## 2. ARIA structural roles

EmptyState uses native HTML elements; ARIA roles are implicit.

| Element | Implicit role | Explicit role override |
|---|---|---|
| Root container | `<div>` (no role) | **None** in M1. See §6 for the recommended `role="status"` wrap when composed inside a stateful host like DataGrid. |
| Icon (lucide `<svg>`) | implicit `img` / decorative | `aria-hidden="true"` (M1 implementation sets this) |
| Title | `<p>` | none |
| Description | `<p>` | none |
| CTA | `<button type="button">` — `button` | none |

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 3. Icon — decorative

The variant icon (Info, CheckCircle2, PlusCircle) is decorative — the
variant's meaning is carried by the title text, NOT by the icon. The icon
is rendered with `aria-hidden="true"` on the `<svg>` element so AT does NOT
announce it.

If the icon were announced ("info icon, empty state, no invoices yet"), the
user would hear the variant twice (once from the icon's accessible name,
once from the title). Hiding the icon avoids the duplication.

**WCAG citation:** WCAG 1.1.1 Non-text Content — decorative imagery MAY be
implemented such that it is ignored by AT.

**M1 baseline:** `aria-hidden="true"` is correctly set on the icon `<svg>` in
the shipping implementation.

---

## 4. Title and description text

Title (`<p class="mt-3 text-base font-medium text-gray-700">`) and
description (`<p class="mt-1 text-sm text-gray-500">`) are plain text content
inside `<p>` elements. They are programmatically determinable by their
native semantics — no ARIA labelling is needed.

**Council open question.** Should the title use a heading element (`<h2>`,
`<h3>`, etc.) instead of `<p>`? Headings give AT users the "list of
headings" navigation pattern, which would let them jump to the empty state.
The downside is that EmptyState renders in many contexts (table cell, panel,
modal, dashboard widget) where the appropriate heading level varies. The M1
baseline uses `<p>` for context-neutrality; a future amendment may add a
`headingLevel?: 1 | 2 | 3 | 4 | 5 | 6` prop that lets hosts elevate the
title.

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships.

---

## 5. CTA button

The CTA is a native `<button type="button">`:

| Requirement | M1 emission |
|---|---|
| Accessible name | the button's text content (the `label` prop value) |
| Keyboard activation | inherited from native `<button>` — Space and Enter both activate |
| Focus ring | `focus:ring-2 focus:ring-blue-500 focus:ring-offset-2` (Tailwind) |
| Disabled state | not supported in M1 — CTA is always enabled when rendered. Hosts that need "this action is unavailable" should conditionally omit `action` rather than render a disabled button. |
| Touch target | `px-4 py-2` padding + `text-sm` font → effective target ≥ 24 × 24 ✓ |

**WCAG citations:**
- WCAG 2.2 SC 2.1.1 Keyboard.
- WCAG 2.2 SC 2.4.7 Focus Visible.
- WCAG 2.2 SC 2.5.8 Target Size (Minimum).

---

## 6. Live-region behaviour (when composed inside a stateful host)

EmptyState in M1 does NOT carry an inherent live region. When the component
is rendered inside a host that transitions from populated → empty (e.g.,
DataGrid when a filter eliminates all rows), the empty-state announcement
is the host's responsibility, NOT EmptyState's.

**Recommendation for hosts.** Wrap EmptyState in a polite live region:

```tsx
<DataGrid
  data={rows}
  columns={cols}
  emptyState={
    <div role="status" aria-live="polite">
      <EmptyState
        variant="informational"
        title="No invoices match your filter"
        description="Try clearing one of the filters above."
      />
    </div>
  }
/>
```

The host owns the live region because:

1. The host knows the politeness level appropriate for the transition.
2. The host knows when the transition fires (data changed vs. initial
   render).
3. EmptyState renders in non-live contexts too (a permanent "empty by
   design" panel like an unconfigured dashboard widget) — wrapping the
   component itself in a live region would over-announce.

**Council open question.** Should EmptyState accept a `live?: 'off' |
'polite' | 'assertive'` prop that opts the root `<div>` into a live region?
Current M1: no. Hosts wrap as needed. A future amendment may add the prop
if usage data shows hosts forget to wrap.

**WCAG citation:** WCAG 2.2 SC 4.1.3 Status Messages.

---

## 7. Focus management

EmptyState's focus surface is the CTA button only (when present). Focus
behaviour is platform-default:

- **Initial render.** The CTA button is NOT auto-focused on mount. Auto-
  focusing the CTA would violate WCAG 2.2 SC 3.2.1 On Focus (changing
  focus on insertion).
- **CTA activation.** When the user clicks or keyboard-activates the CTA,
  the `onClick` handler fires. EmptyState does NOT manage focus across the
  activation — the host's `onClick` handler decides where focus goes next
  (e.g., focusing a newly-rendered form, or the next focusable element
  after EmptyState unmounts).

**Recommendation for hosts.** If the CTA opens a modal / drawer / new form,
the opened surface should focus its first interactive control. If the CTA
triggers data creation (e.g., "Add your first property"), focus should
follow the user's intent — typically into the new edit surface.

**WCAG citation:** WCAG 2.2 SC 3.2.1 On Focus.

---

## 8. Touch targets

| Control | Default size at M1 |
|---|---|
| CTA button | `px-4 py-2` padding (16px horizontal, 8px vertical) + `text-sm` line-height → effective target ≥ 24 × 24 ✓ |

Title and description are not interactive; touch-target requirements don't
apply.

**WCAG citation:** WCAG 2.2 SC 2.5.8 Target Size (Minimum).

---

## 9. Color contrast

Per [EmptyState.Styling §"Visual state inventory"](./EmptyState.Styling.md):

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Title text on host surface background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Description text on host surface background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| CTA label on CTA background | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| CTA border on CTA background | 3:1 | WCAG 2.2 SC 1.4.11 |
| CTA focus ring on host surface | 3:1 | WCAG 2.2 SC 1.4.11 |
| Variant icon | not subject to contrast minimums (decorative; see §3) | — |

**Color is not the only channel.** The variant axis is conveyed primarily
through the title text (which AT announces) and secondarily through the
icon (visual only, hidden from AT). The colour-tinted icon is a visual
reinforcer, NOT the primary signal. **WCAG 2.2 SC 1.4.1 satisfied.**

---

## 10. Reduced motion

EmptyState has no animations in M1. The contract notes this for forward-
compat: any future enter / exit / variant-transition animation MUST honor
`prefers-reduced-motion: reduce`.

**WCAG citation:** WCAG 2.2 SC 2.3.3 Animation from Interactions.

---

## 11. Do / Don't

### Do

- Keep the variant icon `aria-hidden="true"` — the title text carries the
  variant's meaning to AT.
- Use native `<button>` for the CTA so platform activation / focus / AT
  exposure are inherited.
- Provide a clear, scannable title — the title is the primary information
  channel for AT users (the icon is hidden).
- Wrap EmptyState in a `role="status" aria-live="polite"` region when the
  composing host transitions from populated → empty, so AT users are
  notified.
- Verify token contrast pairs in `data-display.tokens.json` before merging
  provider overrides.

### Don't

- Don't remove `aria-hidden="true"` from the icon — AT users would hear the
  variant announced twice.
- Don't auto-focus the CTA on mount; that's a WCAG 2.2 SC 3.2.1 violation.
- Don't elevate the title to a `<h*>` heading without a `headingLevel`
  prop — EmptyState renders in multiple contexts where the appropriate
  heading level differs.
- Don't render a disabled CTA. If an action is conditionally available,
  conditionally render the action — don't show a greyed-out button.
- Don't bake `aria-live` into the EmptyState root; the host owns the
  liveness decision (per §6).

---

## 12. Known gaps

| # | Gap | Fix path |
|---|---|---|
| G1 | Title uses `<p>` — not navigable as a heading via AT "list of headings" | Follow-on PR adds optional `headingLevel?: 1..6` prop (Semantic §7) |
| G2 | No inherent live region — host must wrap when composing inside a stateful surface | Document in hosting components' contracts (DataGrid.Accessibility §8 already names this gap) |
| G3 | No disabled-CTA state — hosts must conditionally render | Intentional; not a gap. Documented in §5. |

---

## 13. Parity notes

- **Blazor (future track):** consumes the same accessibility contract. Icon
  glyphs come from `IHarborlineIconProvider`; the `aria-hidden` rule applies.
- **React (this contract):** as documented.
- **Web Components (Phase M4, Lit):** TBD; the WC track will pass native
  `<button>` through to the shadow tree; live-region semantics work
  cross-shadowdom but require the live-region attribute on the light-DOM
  side or via explicit shadow-DOM ARIA exposure.

---

## References

- ADR 0017 §A1.3 — DataDisplay family contract scope
- [EmptyState.Semantic.md](./EmptyState.Semantic.md) — prop contract
- [EmptyState.Interaction.md](./EmptyState.Interaction.md) — behavioural contract
- [EmptyState.Styling.md](./EmptyState.Styling.md) — token surface + visual states
- [DataGrid.Accessibility.md](./DataGrid.Accessibility.md) — composing surface gap (G5 there names the live-region gap when EmptyState composes inside)
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `button`, `status`, `aria-live`, `aria-hidden`
- WCAG 1.1.1 Non-text Content
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.3.3 Animation from Interactions
- WCAG 2.2 SC 2.4.7 Focus Visible
- WCAG 2.2 SC 2.5.8 Target Size (Minimum)
- WCAG 2.2 SC 3.2.1 On Focus
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 4.1.3 Status Messages
