# SignatureStub — Styling Contract

- **Component:** SignatureStub
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling (token surface, visual states, theming)
- **Status:** Draft
- **Companion contracts:** [Semantic](./SignatureStub.Semantic.md) · [Interaction](./SignatureStub.Interaction.md) · [Accessibility](./SignatureStub.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SignatureStub.tsx`

---

## 1. Purpose

`SignatureStub` is a self-contained signature-capture field: a label, a bordered
`<canvas>` drawing surface with a centered "Sign here" placeholder and a dashed
baseline guide, a typed-name input, an in-built Clear control, and an error message. This contract
names the `--sf-signature-stub-*` token surface, maps each region to the
semantic design token it consumes, pins the Tailwind class recipes, and flags
the one hard-coded value (the pen colour) as a token gap.

All colour comes from the shared semantic token set (`foreground`,
`destructive`, `input`, `background`, `muted`, `muted-foreground`, `border`) —
the component hard-codes no hex **except** the default `penColor`.

---

## 2. Token surface

`SignatureStub` exposes the `--sf-signature-stub-*` family, each aliasing an
existing semantic design token (it does not introduce a parallel palette).

| Token | Semantic role | Maps to | Notes |
|---|---|---|---|
| `--sf-signature-stub-label-fg` | Label text colour | `text-foreground` | `text-sm font-medium` |
| `--sf-signature-stub-required-fg` | Required `*` marker | `text-destructive` | `aria-hidden` decorative marker |
| `--sf-signature-stub-surface-bg` | Canvas surface background | `bg-background` | The drawable surface |
| `--sf-signature-stub-border` | Canvas border (idle) | `border-input` | Default border |
| `--sf-signature-stub-border-error` | Canvas border (error) | `border-destructive` | Replaces the idle border when `error` is set |
| `--sf-signature-stub-disabled-bg` | Canvas background (disabled) | `bg-muted` | Paired with `opacity-50` |
| `--sf-signature-stub-placeholder-fg` | "Sign here" placeholder text | `text-muted-foreground` | `text-xs` |
| `--sf-signature-stub-baseline` | Dashed baseline guide | `border-border` | `border-b border-dashed` |
| `--sf-signature-stub-clear-fg` | Clear control text (idle) | `text-muted-foreground` | `text-xs` |
| `--sf-signature-stub-clear-fg-hover` | Clear control text (hover) | `text-destructive` | `hover:text-destructive` |
| `--sf-signature-stub-error-fg` | Error message text | `text-destructive` | `text-xs`, `role="alert"` |
| `--sf-signature-stub-ink` | Pen stroke colour | **hard-coded `#1e293b`** | ⚠ NOT yet a token — see §5 (token gap G-SS-T1) |

---

## 3. Tailwind class recipes

| Region | Recipe |
|---|---|
| Outer wrapper | `flex flex-col gap-1` (+ host `className`) |
| Label | `text-sm font-medium text-foreground` |
| Required marker | `text-destructive ms-1` (`aria-hidden="true"`) |
| Canvas box (idle) | `relative border rounded-lg overflow-hidden bg-background border-input` |
| Canvas box (error) | …`border-destructive` (replaces `border-input`) |
| Canvas box (disabled) | …`opacity-50 bg-muted` |
| Canvas element | inline `display:block; width:100%; height:auto; touch-action:none` |
| Placeholder | `absolute inset-0 flex flex-col items-center justify-center pointer-events-none` + `text-xs text-muted-foreground` |
| Baseline guide | `absolute bottom-8 start-6 end-6 border-b border-dashed border-border pointer-events-none` |
| Clear control | `self-start text-xs text-muted-foreground hover:text-destructive mt-1` |
| Typed-name group | `flex flex-col gap-1`; label `text-xs font-medium text-foreground` |
| Typed-name input | `w-full rounded-md border border-input bg-background px-3 py-2 text-sm` + shared focus ring and disabled treatment |
| Error message | `text-xs text-destructive` (`role="alert"`) |

The canvas box is sized `width:100%; max-width:{width}px` (the `width` prop caps
the rendered width); the canvas intrinsic resolution is `width`×`height` (px).

---

## 4. Visual state inventory

| State | Trigger | Treatment |
|---|---|---|
| **idle / empty** | no value, enabled | `border-input` box; placeholder "Sign here" shown; Clear hidden |
| **drawn / non-empty** | a stroke exists, enabled | placeholder hidden; Clear control shown (`self-start`) |
| **typed / non-empty** | typed-name input has content | text is rendered into the canvas preview; Clear control shown |
| **error** | `error` set | box border → `border-destructive`; `text-destructive` message with `role="alert"`; co-presents with empty or drawn |
| **disabled** | `disabled` | box `opacity-50 bg-muted`; drawing blocked; Clear hidden; existing `value` still rendered |
| Clear hover | pointer over Clear | text `text-muted-foreground` → `text-destructive` |

Precedence matches [Interaction §5](./SignatureStub.Interaction.md): **disabled**
wins over all; **error** is an independent overlay that can co-present with the
empty/drawn states.

---

## 5. Pen colour token gap

The stroke colour is the `penColor` prop, defaulting to the literal `#1e293b`
(slate-800) in component code — it is **not** sourced from a token (G-SS-T1).
Recommendation: introduce `--sf-signature-stub-ink` aliasing a `foreground`-class
token so the ink re-themes with the palette (and inverts correctly under dark
mode). The stroke MUST retain ≥ 3:1 contrast against `--sf-signature-stub-surface-bg`
so the signature is perceivable (WCAG 2.2 SC 1.4.11, Non-text Contrast); the
baseline guide and placeholder are decorative aids and are exempt from the text
ratio but should remain visible.

---

## 6. Sizing, RTL, reduced motion

- **Sizing:** `width` (default 400) caps the box `max-width` and sets canvas
  intrinsic width; `height` (default 120) sets canvas intrinsic height; the
  canvas renders at `width:100%; height:auto`, so it scales down responsively
  while preserving aspect ratio. Pointer coordinates are rescaled to the
  intrinsic resolution.
- **RTL:** the component already uses **logical** properties — `ms-1`
  (margin-inline-start) on the required marker and `start-6 end-6`
  (inset-inline) on the baseline guide — so it is correct under `dir="rtl"`
  without change. (This is the desired pattern; do not regress to `ml-`/`left`/`right`.)
- **Reduced motion:** no animation; no `prefers-reduced-motion` handling needed.

---

## 7. Do / Don't

### Do
- Consume the semantic tokens listed in §2; let `error`/`disabled` swap the
  border/background per §4.
- Keep the baseline guide + placeholder as `pointer-events-none` decoration.
- Preserve the logical-property recipes (`ms-*`, `start/end`) for RTL.

### Don't
- Don't hard-code the pen colour at call sites — pass `penColor` from a resolved
  token, and migrate the default to `--sf-signature-stub-ink` (G-SS-T1).
- Don't put hex values in component code for any region other than the (to-be-
  tokenised) pen default.
- Don't replace the logical insets with physical `left`/`right` — that breaks RTL.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
