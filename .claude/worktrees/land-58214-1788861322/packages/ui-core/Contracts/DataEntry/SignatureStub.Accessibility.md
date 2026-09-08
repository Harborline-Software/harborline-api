# SignatureStub — Accessibility Contract

- **Component:** SignatureStub
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Draft
- **Companion contracts:** [Semantic](./SignatureStub.Semantic.md) · [Interaction](./SignatureStub.Interaction.md) · [Styling](./SignatureStub.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/SignatureStub.tsx`

---

## 1. Purpose

`SignatureStub` pairs its HTML5 `<canvas>` with a native typed-name input.
Canvas drawing remains a pointer enhancement; the typed path is the keyboard
and assistive-technology equivalent and emits the same PNG data URL through
`onChange`.

Every requirement is keyed to a WCAG 2.2 AA success criterion.

---

## 2. Roles & ARIA wiring

| Element | Role / attribute | Notes |
|---|---|---|
| `<canvas>` | `role="img"`; named by `aria-labelledby` or fallback `aria-label` | Described by capture instructions and the error when present; includes fallback text |
| Visible field label | `id` referenced by the canvas | Programmatic canvas name when `label` is provided |
| Typed-name `<input>` | native `textbox` with `<label for>` | Keyboard-native capture path; uses `aria-required`, `aria-invalid`, and `aria-describedby` as applicable |
| Required `*` marker | `aria-hidden="true"` | Decorative; an adjacent `sr-only` "(required)" and input `required` semantics carry meaning |
| Error `<p>` | `role="alert"` | Announced on appearance and linked to both capture controls |
| Clear `<button type="button">` | implicit `button` | Accessible name from its text "Clear signature" |

**WCAG citations:** SC 1.1.1 Non-text Content (the canvas has an `aria-label`
but no text alternative for the captured signature itself); SC 4.1.2 Name, Role,
Value.

---

## 3. Accessible name

The canvas accessible name is the `label` prop through `aria-labelledby`,
falling back to `"Signature pad"` when `label` is omitted. The typed input has
its own visible label, "Your name (as signature)", because it is an actionable
form control rather than a second label for the canvas.

**WCAG citation:** SC 4.1.2 Name, Role, Value.

---

## 4. Keyboard model

| Key | Behaviour |
|---|---|
| Tab / Shift+Tab | Moves through the typed-name input and the **Clear signature** button when present |
| Character/editing keys | Edit the native typed-name input using platform conventions |
| Enter / Space | Activates the focused Clear button |

Canvas drawing is pointer-only, while the default typed input provides the
equivalent keyboard path. On each typed change the name is rendered into the
canvas and emitted as PNG; emptying the input emits `null`. Touch handlers call
`preventDefault()` (`touch-action: none`) so drawing does not scroll the page.

`showTypedInput={false}` is conformant only when a host supplies and documents
an equivalent keyboard capture path.

---

## 5. Required-state semantics

`required` renders a visual `*` marker that is `aria-hidden="true"` plus an
`sr-only` "(required)" suffix. The typed input uses native `required`. The
canvas does not carry `aria-required`, because that state is not valid for its
`img` role.

**WCAG citation:** SC 3.3.2 Labels or Instructions.

---

## 6. Error announcement

The error message renders in a `<p role="alert">`, so AT announces it when it
appears. Its id is included in `aria-describedby` for the canvas and typed
input; both also expose `aria-invalid="true"`.

**WCAG citations:** SC 4.1.3 Status Messages (the `role="alert"` channel);
SC 3.3.1 Error Identification (association gap).

---

## 7. Disabled state

When `disabled`, drawing is blocked, the surface dims (`opacity-50 bg-muted`),
the typed input is natively disabled, and the Clear button is hidden; any
existing `value` still renders. The canvas carries `aria-disabled="true"`.

---

## 8. Colour & contrast

| Surface | Token | Minimum ratio | WCAG |
|---|---|---|---|
| "Sign here" placeholder on surface | `text-muted-foreground` on `bg-background` | 4.5:1 | SC 1.4.3 — VERIFY (muted-foreground can be borderline) |
| Error message on surface | `text-destructive` on `bg-background` | 4.5:1 | SC 1.4.3 |
| Canvas border (error vs idle) | `border-destructive` / `border-input` | 3:1 | SC 1.4.11 Non-text Contrast |
| Pen stroke on surface | `penColor` (`#1e293b`) on `bg-background` | 3:1 | SC 1.4.11 (the signature must be perceivable) |

The error state is signalled by **both** colour (border + text) and the
`role="alert"` text message, so it does not rely on colour alone (SC 1.4.1). The
dashed baseline guide is decorative.

---

## 9. Touch target

The canvas is a large pointer target. The **Clear** control uses at least the
24×24 CSS-px minimum and the shared explicit `focus-visible` ring (SC 2.5.8,
SC 2.4.7).

---

## 10. Known gaps

| Gap ID | Severity | Description | Fix path |
|---|---|---|---|
| G-SS1 | Low | Hosts can hide the built-in typed input | `showTypedInput={false}` requires a documented host-owned equivalent |
| G-SS2 | Low | Canvas pixel content cannot expose the captured strokes as structured AT content | Accessible name, fallback text, instructions, and the typed equivalent provide the required non-visual path |

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |
