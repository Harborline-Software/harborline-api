# FormField — Accessibility Contract

- **Component:** FormField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Accessibility (ARIA, keyboard, focus, screen-reader)
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FormField.Semantic.md) · [Interaction](./FormField.Interaction.md) · [Styling](./FormField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormField.tsx` + `FormFieldContext.tsx`
- **Catalog rows:** #55 FieldWrapper / #63 Form (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Purpose

FormField is the **WCAG 2.2 AA compliance mechanism** for `@harborline-software/ui-react`
field primitives. Every form control wrapped in a FormField gets:

1. A native `<label htmlFor>` linkage to the child input — the foundational
   programmatic label per WCAG 2.2 SC 1.3.1 + SC 4.1.2.
2. An `aria-describedby` linkage to a hint node and/or an error node, threaded
   to the child input via `FormFieldContext` / `useFormField()` — so AT
   announces the supplementary message together with the input's name.
3. A `role="alert"` live region on the error message — so AT announces error
   transitions politely-but-promptly.

This contract names each mechanism, the WCAG citation, and the rules child
inputs MUST follow to participate correctly. The `aria-describedby` thread is
the **core accessibility mechanism** of the entire Batch B field family —
this contract is the source of truth.

---

## 2. ARIA structural roles

FormField uses native HTML elements; ARIA roles are implicit.

| Element | Implicit role | Explicit override |
|---|---|---|
| Root container | `<div>` (no role) | none — FormField is a layout grouping, not a landmark |
| Label | `<label>` (via `@radix-ui/react-label`'s `Label.Root` which renders a native `<label>`) | none |
| Required marker | `<span aria-hidden="true">` | `aria-hidden="true"` explicitly |
| Slot wrapper | `<div>` (no role) | none |
| Hint text | `<p>` with `id={name}-hint` | none — referenced by child via `aria-describedby` |
| Error text | `<p>` with `id={name}-error` + `role="alert"` | `role="alert"` (implicit polite live region with assertive politeness) |

**WCAG citation:** WCAG 2.2 SC 4.1.2 Name, Role, Value.

---

## 3. Label-input linkage

The label element renders with `htmlFor={name}`. The child input MUST set
`id={name}` on its root focusable element (the native `<input>`, the
`<button>` of a Radix Select trigger, the Radix Checkbox root, etc.).

When the linkage is wired:

- Clicking the label focuses the child input (native behaviour).
- AT announces the label text when focus enters the child input.
- The label is programmatically associated with the input, satisfying
  WCAG 2.2 SC 1.3.1 Info and Relationships.

**Adapter responsibility.** Every Batch B field primitive (TextField,
SelectField, DateField, NumberField, CheckboxField) MUST set `id={name}` on
the focusable root. The shipping implementations all do — this contract pins
the requirement.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships.
- WCAG 2.2 SC 4.1.2 Name, Role, Value.
- WCAG 2.2 SC 2.5.3 Label in Name (the visible label text matches the
  accessible name — native `<label htmlFor>` linkage satisfies this).

---

## 4. `aria-describedby` thread — core accessibility mechanism

FormField computes a `describedBy` string and threads it to the child input
via `FormFieldContext`:

```typescript
// Inside FormField.tsx:
const hintId = hint ? `${name}-hint` : undefined
const errorId = error ? `${name}-error` : undefined
const describedBy = [hintId, errorId].filter(Boolean).join(' ') || undefined
```

The child input reads `describedBy` from `useFormField()` and renders:

```typescript
// Inside every Batch B field primitive:
const { describedBy } = useFormField()
return <input ... aria-describedby={describedBy || undefined} />
```

The string is space-separated id list per the ARIA spec — multiple ids
(both hint AND error) are valid simultaneously, though the M1 implementation
suppresses hint when error is supplied (so describedBy resolves to
`errorId` only in error mode).

### 4.1 Multiple-ids precedence (RA-12, 2026-06-06)

When both `hint` and `error` are supplied:

- Both `<p>` elements are present in the DOM — the hint element as secondary
  supporting text, the error element as the primary supporting line.
- `describedBy` resolves to `"{name}-hint {name}-error"` (both ids, space-
  separated), so AT reads both messages when focus enters the child input.
- No dangling ids: each id in `describedBy` corresponds to a rendered DOM node.

### 4.2 Child-input opt-out

A child input that has its OWN `aria-describedby` (e.g., a richer description
node the host wires manually) can ignore the `describedBy` from context.
Recommended pattern for hosts: prefer FormField's wrapping over custom
`aria-describedby` — the wrapper handles the id management automatically.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships.
- WCAG 2.2 SC 3.3.2 Labels or Instructions.

---

## 5. Error message — `role="alert"`

The error `<p>` is rendered with `role="alert"`. The `alert` role is an
**implicit live region with `aria-live="assertive"` politeness** — when the
error message appears (or its text content changes), AT interrupts the
user's current speech to announce it.

| Scenario | M1 behaviour |
|---|---|
| `error` becomes defined for the first time (e.g., user submits invalid form) | AT announces the error message assertively |
| `error` text changes (validation re-fires with a different message) | AT announces the new message assertively |
| `error` becomes undefined (validation passes) | the `<p>` is unmounted; no announcement (AT does not announce "error cleared" — silence is the correct cadence) |
| `hint` changes | the `<p>` is replaced; AT does NOT announce hint changes (the hint `<p>` does NOT carry a live region) |

**Politeness rationale.** Form-validation errors are time-sensitive (the user
just submitted; they need to know which field failed). `assertive` is the
correct politeness for blocking errors. Hint changes are not time-sensitive
and would over-announce on every render.

**Council open question.** Some design systems prefer `role="status"` with
`aria-live="polite"` for error messages — the rationale is that
`role="alert"` interrupts the user's current speech, which can feel jarring.
Current contract: `role="alert"` matches the shipping implementation. Revisit
if user-research surfaces the interruption concern.

**WCAG citations:**
- WCAG 2.2 SC 4.1.3 Status Messages.
- WCAG 2.2 SC 3.3.1 Error Identification.

---

## 6. Required marker

The required-asterisk `<span>` is `aria-hidden="true"`. The semantic
requiredness is carried by the child input's HTML `required` attribute (which
hosts MUST set on inputs they wrap in a FormField with `required={true}`).

| Concern | M1 emission |
|---|---|
| Visual required-marker (asterisk) | rendered, `aria-hidden="true"` |
| Programmatic required-ness | the child input's native `required` attribute (NOT set by FormField; the host must pass through) |

**Adapter responsibility — known gap.** FormField does NOT currently propagate
`required` to the child input automatically. Hosts who set `required` on
FormField must ALSO set `required` on the child input. **Gap G1** (§10):
a follow-on amendment SHOULD add an opt-in prop-forwarding pattern (or
document the pattern more loudly in the Semantic contract).

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships (programmatic requiredness).
- WCAG 2.2 SC 1.4.1 Use of Color (requiredness conveyed through TWO channels
  — the asterisk visual AND the input's native required state).

---

## 7. Keyboard navigation

FormField has no keyboard handlers of its own. Keyboard navigation is the
child input's native behaviour:

| Key | Behaviour |
|---|---|
| Tab | Move focus through the form's tab order — the child input is the focus stop, NOT the label or the wrapper. |
| Shift+Tab | Reverse order. |
| Click on label | Focuses the child input (native `<label htmlFor>` behaviour). |

The wrapper itself is not focusable; FormField has no `tabIndex`.

**WCAG citation:** WCAG 2.2 SC 2.1.1 Keyboard.

---

## 8. Focus management

FormField does not manage focus. Focus behaviour is the child input's native
behaviour:

- Initial focus on form submit fails: typical pattern is the host focuses
  the first FormField with an error. The host implements this; FormField
  does not auto-focus on error.
- Re-render on hint / error change: the child input retains focus (the DOM
  node is not replaced).

**WCAG citation:** WCAG 2.2 SC 3.2.1 On Focus, SC 3.2.2 On Input — no
context change on render.

---

## 9. Color contrast

Per [FormField.Styling §"Visual state inventory"](./FormField.Styling.md):

| Surface | Minimum ratio | WCAG citation |
|---|---|---|
| Label text on host surface | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Hint text on host surface | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Error text on host surface | 4.5:1 | WCAG 2.2 SC 1.4.3 |
| Required-marker asterisk on host surface | 3:1 | WCAG 2.2 SC 1.4.11 (non-text; aria-hidden makes it decorative but visual contrast still required) |

**Color is not the only channel.** The error state is conveyed through:
(1) the error text content (announced via `role="alert"`), (2) the child
input's `aria-invalid="true"`, (3) the child input's red border + red focus
ring (visual). **WCAG 2.2 SC 1.4.1 satisfied.**

Requiredness is conveyed through: (1) the visual asterisk, (2) the child
input's native `required` attribute (announced by AT). **SC 1.4.1
satisfied** (assuming the host sets `required` on the child per §6).

---

## 10. Known gaps

| # | Gap | Fix path |
|---|---|---|
| G1 | FormField doesn't auto-propagate `required` to the child input | Follow-on PR adds opt-in prop-forwarding OR documents the pattern more loudly in Semantic |
| G2 | ~~When both `hint` and `error` are supplied, hint is suppressed~~ | RESOLVED (RA-12, 2026-06-06) — both nodes rendered; both ids in `describedBy` |
| G3 | `role="alert"` may feel intrusive for low-priority validation errors | Council decision; M1 baseline is `role="alert"` matching the implementation |
| G4 | Hosts must remember to set `error={Boolean(errors[name])}` on the child input in parallel with FormField's `error={errors[name]}` (boolean / string mismatch) | Documented in Styling §5 + Semantic §3.1; library-side coordination is future work |

---

## 11. Do / Don't

### Do

- Use the wrapper for every form-field instance. Wiring `aria-describedby`
  by hand outside FormField is error-prone.
- Pair FormField's `error` (string) with the child input's `error`
  (boolean) so both the visual and the programmatic error state align.
- Keep `aria-hidden="true"` on the required asterisk; rely on the child's
  native `required` for AT announcement.
- Use `role="alert"` on the error `<p>` for time-sensitive validation
  feedback; revisit per-form if assertion is too aggressive.
- Verify token contrasts in `forms.tokens.json` before merging provider
  overrides.

### Don't

- Don't set `aria-required` on the wrapper — requiredness lives on the
  child input's native `required` attribute, not on the wrapper.
- Do render both hint and error simultaneously (RA-12, 2026-06-06) — both
  nodes are in the DOM; error is primary, hint is secondary.
- Don't add a custom `aria-describedby` to the child input on top of
  FormField's threaded value — they collide. Prefer FormField's auto-
  threading; opt out only when truly needed (and document why).
- Don't bake the asterisk into the label text itself (e.g., `"Email *"` as
  the `label` string). The asterisk is a structural marker, not part of
  the label.

---

## 12. Parity notes

- **Blazor (future HarborlineForm / HarborlineFieldWrapper track):** consumes
  the same accessibility contract. Label is a native `<label>` with
  `for={name}`; `aria-describedby` is set on the child input via a
  cascading parameter analogous to React Context.
- **React (this contract):** uses `@radix-ui/react-label`'s `Label.Root`
  primitive (renders a native `<label>` with framework-native `htmlFor`)
  and a React Context for `describedBy`.
- **Web Components (Phase M4, Lit):** TBD; the WC track passes
  `describedBy` via attribute reflection or shadow-DOM ARIA exposure.

---

## References

- ADR 0017 §A1.3 — DataEntry (Forms) family contract scope
- [FormField.Semantic.md](./FormField.Semantic.md) — prop contract
- [FormField.Interaction.md](./FormField.Interaction.md) — behavioural contract
- [FormField.Styling.md](./FormField.Styling.md) — token surface + visual states
- `_shared/design/accessibility.md` — fleet WCAG 2.2 AA baseline
- WAI-ARIA 1.2 — `alert`, `aria-describedby`, `aria-hidden`, `aria-invalid`, `aria-required`
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 1.4.1 Use of Color
- WCAG 2.2 SC 1.4.3 Contrast (Minimum)
- WCAG 2.2 SC 1.4.11 Non-text Contrast
- WCAG 2.2 SC 2.1.1 Keyboard
- WCAG 2.2 SC 2.5.3 Label in Name
- WCAG 2.2 SC 3.2.1 On Focus
- WCAG 2.2 SC 3.2.2 On Input
- WCAG 2.2 SC 3.3.1 Error Identification
- WCAG 2.2 SC 3.3.2 Labels or Instructions
- WCAG 2.2 SC 4.1.2 Name, Role, Value
- WCAG 2.2 SC 4.1.3 Status Messages

---

## Wave FR-1.4 — required/disabled ARIA (Accessibility)

_Ruling: FR-1.4 (family-rulings-2026-06-11.md). Pattern: DataGrid #1022._
_Status: Draft._

### FR-1.4.1 Context fields and ARIA responsibility

FR-1.4 adds `required` and `disabled` to `FormFieldContextValue`. The ARIA
responsibility split between FormField and its child inputs is:

| Concern | ARIA attribute | Responsibility |
|---|---|---|
| Programmatic requiredness | `aria-required="true"` (or native `required`) | **Child input** — reads `effectiveRequired` from context + local prop (logical OR); emits on its root focusable element |
| Visual required-marker | `<span aria-hidden="true">*</span>` | **FormField** — unchanged from M1; the asterisk is `aria-hidden` and decorative |
| Programmatic disabled state | `aria-disabled="true"` (or native `disabled`) | **Child input** — reads `effectiveDisabled` from context + local prop (logical OR); emits on its root focusable element |
| Disabled chrome dimming | None (visual only) | **FormField** — applies `--sf-field-*-disabled` tokens to label / hint |

**FormField does NOT emit `aria-required` or `aria-disabled` on its own
root `<div>`.** The wrapper is a layout grouping, not a form control. Adding
these attributes to the wrapper would misrepresent the wrapper as
control-like to AT.

### FR-1.4.2 Child-input `aria-required` rule

When `effectiveRequired === true`, the child input MUST emit **exactly one**
of the following — not both:

1. Native `required` attribute on a native `<input>` / `<select>` /
   `<textarea>` element. (Preferred for native elements — browser-native
   announcement, form-submission validation.)
2. `aria-required="true"` on the root focusable element when the control is
   not a native form element (e.g., a Radix Select trigger `<button>`, a
   canvas-based input, a custom `role="combobox"` element).

Do NOT set both `required` and `aria-required="true"` on the same element —
some AT announces the state twice.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships (programmatic form semantics).
- WCAG 2.2 SC 3.3.2 Labels or Instructions (required fields must be
  indicated before the user submits).

### FR-1.4.3 Child-input `aria-disabled` rule

When `effectiveDisabled === true`, the child input MUST emit the native
`disabled` attribute (for native form elements) or `aria-disabled="true"` (for
custom interactive elements). The implementation choice drives AT announcement:

- Native `disabled` removes the element from the tab order and announces it
  as "dimmed"/"unavailable" in AT. Use for native `<input>`, `<button>`,
  `<select>`, `<textarea>`.
- `aria-disabled="true"` keeps the element focusable (AT can still reach it
  to announce "dimmed/unavailable") but the element does not receive pointer
  events (the component must also add `pointer-events: none` or equivalent).
  Use for custom interactive composites (Radix Select triggers, Radix
  Checkbox, canvas-based inputs) where focus reachability is still desired.

The contract does not mandate which form — each child component's
Accessibility contract specifies the correct form for that component.

**WCAG citations:**
- WCAG 2.2 SC 4.1.2 Name, Role, Value (state must be programmatically
  determinable).

### FR-1.4.4 Known-gap update (G1 resolved)

Gap G1 from §10 ("FormField doesn't auto-propagate `required` to the child
input") is resolved by FR-1.4 context threading. The child input reads
`required` from context via the logical-OR rule (Semantic FR-1.4.3) —
hosts no longer need to set `required` redundantly on both FormField and the
child input.

Update to §10 gap table:

| # | Gap | Status |
|---|---|---|
| G1 | FormField doesn't auto-propagate `required` to the child input | RESOLVED — FR-1.4 (2026-06-11): `required` is now threaded via context; child inputs consume via logical OR |

### FR-1.4.5 ESignatureField `required` → `aria-required` note

ESignatureField.Semantic §7 documents that it reads `required` from context
and applies it as `aria-required` on the acceptance checkbox. This usage is
compliant with FR-1.4.2 rule 2 above (the acceptance `<input type="checkbox">`
is a native element and should use native `required` rather than
`aria-required` when possible; if the checkbox is replaced with a custom
element, `aria-required="true"` applies). The ESignatureField implementation
should choose the appropriate form for its acceptance checkbox type.
