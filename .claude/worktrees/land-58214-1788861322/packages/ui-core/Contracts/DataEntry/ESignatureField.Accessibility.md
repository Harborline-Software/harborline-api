# ESignatureField — Accessibility Contract

- **Component:** ESignatureField
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ESignatureField.Semantic.md) · [Interaction](./ESignatureField.Interaction.md) · [Accessibility](./ESignatureField.Accessibility.md) · [Styling](./ESignatureField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ESignatureField.tsx` (not yet implemented)
- **Catalog row:** #A20 ESignatureField (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation)

---

## 1. Purpose

ESignatureField captures a legally-binding electronic signature and must be
accessible to keyboard-only users and screen reader users. The draw method uses
the same canvas-based `Signature` widget that is inaccessible in isolation
(WCAG 2.2 SC 1.1.1 and 2.1.1 violations documented in Signature.Accessibility §3).
ESignatureField closes these gaps by:

1. Providing a `type` method that is fully keyboard and AT-accessible as the
   keyboard-native alternative to drawing.
2. Providing a `upload` method that relies on a standard file-input (natively
   keyboard-accessible).
3. Structuring the method tabs and all surrounding controls with full ARIA
   semantics.

The `draw` canvas method remains inaccessible in isolation. For WCAG compliance,
`methods` MUST always include at least one of `'type'` or `'upload'`. Omitting
both and using `methods={['draw']}` alone reproduces the Signature component's
accessibility violations and is NOT compliant for legal signature use cases.

---

## 2. ARIA structural roles — method selector (tab panel)

The method selector follows the ARIA Authoring Practices Guide
[Tabs Pattern](https://www.w3.org/WAI/ARIA/apg/patterns/tabs/) with manual
activation.

```
<div role="tablist" aria-label="Signature method" aria-labelledby="{formFieldId}">
  <button role="tab" id="esig-tab-draw-{uid}" aria-selected="true|false"
          aria-controls="esig-panel-draw-{uid}">Draw</button>
  <button role="tab" id="esig-tab-type-{uid}" aria-selected="true|false"
          aria-controls="esig-panel-type-{uid}">Type</button>
  <button role="tab" id="esig-tab-upload-{uid}" aria-selected="true|false"
          aria-controls="esig-panel-upload-{uid}">Upload image</button>
</div>

<div role="tabpanel" id="esig-panel-draw-{uid}"
     aria-labelledby="esig-tab-draw-{uid}"
     tabIndex="0">
  <!-- Signature canvas -->
</div>
<!-- inactive panels have hidden attribute -->
```

`{uid}` is a stable per-instance unique ID (e.g. from `React.useId()`). The
same uid-scoping prevents ARIA id collisions when multiple ESignatureField
instances appear on a page (e.g. a two-party signing workflow).

Inactive panels carry `hidden` (not just CSS `display:none`). AT must not
reach into hidden panels.

When only one method is offered, the `role="tablist"` and `role="tab"` structure
is omitted entirely. The single panel is rendered as a plain `<div>` with an
`aria-label` describing the capture method.

---

## 3. Canvas (draw method) — accessibility treatment

The `Signature` canvas widget has no native AT support (documented gap G1 in
Signature.Accessibility §6). ESignatureField must augment it as follows:

### 3.1 Canvas role and label

```
<canvas
  role="img"
  aria-label="Signature drawing area"
  aria-describedby="esig-draw-instructions-{uid}"
/>
```

### 3.2 Canvas fallback content

The `<canvas>` element must contain descriptive fallback text for AT that
does not support canvas:

```html
<canvas ...>
  Signature drawing area. Use a mouse or touch screen to draw your signature.
  For keyboard access, switch to the Type tab.
</canvas>
```

**Safari + VoiceOver limitation:** Safari does not reliably read fallback text placed *inside* `<canvas>`. The primary AT description for the draw surface MUST come from the `aria-describedby` pointing to the instructions paragraph (§3.3), not from this in-canvas fallback. The in-canvas text remains for other ATs and for progressive enhancement; do NOT rely on it as the sole AT communication channel.

### 3.3 Instructions paragraph

An instructions paragraph (visually below or near the canvas) carries the
`id="esig-draw-instructions-{uid}"` referenced by `aria-describedby`. Text:

> "Draw your signature in the box above. Use the Clear button to start over.
> If you are using a keyboard or screen reader, switch to the Type tab to
> enter your name instead."

The instructions paragraph is always rendered (not hidden) but may be styled
with `sr-only` if the visual design omits it — it must remain in the DOM for AT.

### 3.4 AT alternative recommendation

Because keyboard drawing on a canvas is not feasible, ESignatureField should
communicate the typed alternative to AT proactively. When the active method is
`draw` and both `draw` and `type` are available, an `aria-live="polite"` region
announces once on initial render:

> "Keyboard users: activate the Type tab to enter your name as a signature."

This announcement fires once per component mount, not on every render.

---

## 4. Typed-name input (type method)

```
<label htmlFor="esig-type-input-{uid}">Your name (as signature)</label>
<input
  type="text"
  id="esig-type-input-{uid}"
  aria-describedby="esig-type-preview-desc-{uid}"
  autocomplete="name"
  spellcheck="false"
/>

<div
  id="esig-type-preview-desc-{uid}"
  aria-live="polite"
  aria-atomic="true"
>
  <!-- Announces "Signature preview: {name}" when name is non-empty -->
</div>
```

The live region announces the rendered name so screen reader users know
their typed text has been accepted as the signature image. It fires only when
the value transitions from empty to non-empty (not on every keystroke) to
avoid AT chatter.

---

## 5. Upload method

```
<div
  role="region"
  aria-label="Upload signature image"
  aria-describedby="esig-upload-instructions-{uid}"
>
  <p id="esig-upload-instructions-{uid}">
    Upload a PNG, JPEG, WebP, or SVG image of your handwritten signature.
  </p>

  <input
    type="file"
    id="esig-upload-input-{uid}"
    accept="image/png,image/jpeg,image/webp,image/svg+xml"
    aria-label="Choose signature image file"
  />
  <label htmlFor="esig-upload-input-{uid}">Browse…</label>
</div>
```

After a file is selected, an `aria-live="polite"` region announces the outcome:

- Success: "Signature image uploaded: {filename}"
- Validation error: "File rejected: {reason}. Please upload a PNG, JPEG,
  WebP, or SVG image."

When the uploaded image thumbnail is displayed, it carries:

```
<img
  src="{dataUrl}"
  alt="Uploaded signature image preview"
  aria-describedby="esig-upload-instructions-{uid}"
/>
```

The Remove button:

```
<button aria-label="Remove uploaded signature image">Remove</button>
```

---

## 6. Signer identity fields

Each identity field is a standard labelled `<input>`:

```
<label htmlFor="esig-name-{uid}">Full name</label>
<input id="esig-name-{uid}" type="text" autocomplete="name" />

<label htmlFor="esig-title-{uid}">Title</label>
<input id="esig-title-{uid}" type="text" autocomplete="organization-title" />

<label htmlFor="esig-date-{uid}">Date</label>
<input id="esig-date-{uid}" type="date" aria-readonly="{!signerDateEditable}" />
```

When a field is in `requiredFields`, it carries `aria-required="true"`.

Inline validation messages (when a required field is empty at submission
attempt) are rendered in a sibling element with `role="alert"` so they are
announced immediately. Each field's `aria-describedby` links to its error
element when an error is active.

---

## 7. Acceptance checkbox

The acceptance checkbox is a standard `<input type="checkbox">` (or Radix
`<Checkbox.Root>` styled equivalent):

```
<input
  type="checkbox"
  id="esig-accept-{uid}"
  aria-required="{required from FormFieldContext}"
  aria-describedby="esig-accept-hint-{uid}"
  aria-label="{acceptanceLabel}"
/>
<label htmlFor="esig-accept-{uid}">{acceptanceLabel}</label>
```

The `aria-describedby` points at a hint paragraph (if present) explaining
the legal implication of checking. The hint text is always rendered in the
DOM (not hidden) and may carry `class="sr-only"` if the visual design
omits it.

When `required` is active from `FormFieldContext`, the acceptance checkbox
has `aria-required="true"`.

---

## 8. Complete state

On entering `complete`, an `aria-live="assertive"` region announces:

> "Signature accepted. Your signature has been recorded."

This live region persists in the DOM at all times (just with empty content
until `complete`) to avoid AT re-discovering a newly inserted region.

The "Clear and re-sign" button is announced with full context:

```
<button aria-label="Clear signature and sign again">Clear and re-sign</button>
```

---

## 9. Disabled state

- `disabled` on all interactive child elements (inputs, buttons, canvas
  interaction blocked).
- No `aria-disabled` alone — use the native `disabled` attribute so AT
  communicates the state without requiring keyboard focus.
- The root container MAY carry `aria-disabled="true"` as an informational
  marker for compound-widget assistive tools.

---

## 10. FormFieldContext wiring

| Context value | Applied to |
|---|---|
| `id` (FormField label ID) | `aria-labelledby` on the root `role="region"` + the `role="tablist"` |
| `describedBy` | `aria-describedby` on the root `role="region"` (FormField hint/error IDs) |
| `required` | `aria-required="true"` on the acceptance checkbox |
| `disabled` | All child interactives |
| `error` | A visible error treatment on the root; the associated FormField error `<p>` already carries the error ID referenced by `describedBy` |

The root container:

```
<div
  role="region"
  aria-labelledby="{formField.id}"
  aria-describedby="{formField.describedBy}"
>
  ...
</div>
```

---

## 11. WCAG compliance summary

| Criterion | Requirement | How satisfied |
|---|---|---|
| 1.1.1 Non-text Content | Canvas has text alternative | `<canvas>` fallback text + `role="img" aria-label` + instructions `aria-describedby` |
| 1.3.1 Info and Relationships | Labels programmatically associated | All inputs have `<label>` or `aria-label`; ARIA roles on tab panel |
| 1.3.5 Identify Input Purpose | Identity inputs have `autocomplete` | `autocomplete="name"` / `"organization-title"` |
| 2.1.1 Keyboard | All operations keyboard-accessible | Typed and upload methods are keyboard-native; draw has keyboard-accessible alternative |
| 2.1.2 No Keyboard Trap | Tab out of component is always possible | Standard tab order; no trap in canvas or upload zone |
| 2.4.3 Focus Order | Logical focus sequence | Tab order documented in Interaction contract §10 |
| 2.4.7 Focus Visible | Focus ring on all interactive elements | See Styling contract §8 |
| 3.3.1 Error Identification | Errors described in text | Inline `role="alert"` paragraphs for required-field validation |
| 3.3.2 Labels or Instructions | Inputs labelled | All inputs have associated labels; canvas has instructions paragraph |
| 4.1.2 Name, Role, Value | ARIA roles correct | `tablist`/`tab`/`tabpanel`; `region`; acceptance `checkbox` |

---

## 12. Known accessibility gaps at forward-spec stage

| # | Gap | Severity | Fix path |
|---|---|---|---|
| A1 | Draw method canvas is not keyboard-drawable | Accepted trade-off | AT users must use `type` or `upload` method |
| A2 | Off-screen canvas for typed-name image may not render correctly in all browser/AT combinations | Medium | Test with NVDA + Chrome; fallback to SVG-text approach if canvas-font fails |
| A3 | Drag-and-drop zone does not support keyboard file drop | Medium | Keyboard users use Browse button; drag-drop is a pointer enhancement only |
| A4 | No `aria-disabled` on method tabs when `complete` (tabs are frozen) | Medium | Add `aria-disabled="true"` on tab buttons when in `complete` state |
