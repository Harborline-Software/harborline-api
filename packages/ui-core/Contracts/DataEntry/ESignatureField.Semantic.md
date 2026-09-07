# ESignatureField — Semantic Contract

- **Component:** ESignatureField
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ESignatureField.Interaction.md) · [Accessibility](./ESignatureField.Accessibility.md) · [Styling](./ESignatureField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ESignatureField.tsx` (not yet implemented)
- **Catalog row:** #A20 ESignatureField (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation)

---

## 1. Purpose

ESignatureField is a legally-oriented electronic signature capture component. It
wraps and extends the `Signature` canvas widget by adding three capture methods,
an acceptance confirmation checkbox, optional signer identity fields, and a
structured `SignatureResult` value model. It integrates with `FormFieldContext`
for label linkage, error presentation, and AT description in the same way as
other DataEntry fields.

ESignatureField is suitable wherever a legally binding electronic signature must
be collected: lease execution, work-order authorization, contract acknowledgement.
It is NOT a raw drawing tool — use `Signature` directly when the structured
workflow concerns (acceptance checkbox, identity fields, method selector) are not
wanted.

---

## 2. Data model

### 2.1 `SignatureResult`

The externally-visible value type. Produced by `onChange` and `onComplete`.

```typescript
interface SignatureResult {
  method: 'draw' | 'type' | 'upload'
  imageData: string        // SVG string (draw) or PNG data URL (type/upload)
  signerName?: string      // from the signer identity section, if shown
  signerTitle?: string     // from the signer identity section, if shown
  timestamp: string        // ISO 8601 datetime string (UTC)
  accepted: boolean        // whether the acceptance checkbox is checked
}
```

`imageData` is always a non-empty string when `accepted === true` and the
component is in the `complete` state. It is `''` while signing is in progress.

### 2.2 Internal state shape (informative)

The implementation is free to manage any internal state it needs. The observable
contract is the `SignatureResult` emitted to `onChange`. Informative sketch:

```typescript
type ESignatureState = 'idle' | 'signing' | 'complete' | 'cleared'
type SignatureMethod = 'draw' | 'type' | 'upload'
```

---

## 3. Props — full interface

```typescript
interface ESignatureFieldProps {
  // --- Core value ---
  value?: SignatureResult
  onChange?: (result: SignatureResult) => void
  onComplete?: (result: SignatureResult) => void

  // --- Method selector ---
  methods?: SignatureMethod[]             // default ['draw', 'type', 'upload']
  defaultMethod?: SignatureMethod         // default 'draw'

  // --- Acceptance section ---
  acceptanceLabel?: string
  // default "I agree that this electronic signature is legally binding"

  // --- Signer identity ---
  showSignerName?: boolean                // default false
  showSignerTitle?: boolean               // default false
  showSignerDate?: boolean                // default false
  signerDateEditable?: boolean            // default false (today pre-filled, read-only)

  // --- Signing context (informational display) ---
  documentTitle?: string
  signerRole?: string                     // e.g. "Tenant", "Landlord", "Witness"
  requiredFields?: Array<'name' | 'title' | 'date'>

  // --- Canvas (draw method) passthrough ---
  canvasHeight?: number                   // default 160; forwarded to Signature
  signatureFormat?: 'svg' | 'png'         // default 'svg'; forwarded to Signature

  // --- Typed signature ---
  typedFontFamily?: string
  // default 'Dancing Script, Brush Script MT, cursive'

  // --- Field-level ---
  disabled?: boolean
  error?: boolean
  className?: string
}
```

---

## 4. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `value` | `SignatureResult` | — | Controlled value. When provided, the component reflects this state. See §4.1. |
| `onChange` | `(result: SignatureResult) => void` | — | Fires on every change to any sub-field (method switch, canvas stroke, typed name, upload, acceptance toggle, identity field edits). Delivers the full `SignatureResult` snapshot. |
| `onComplete` | `(result: SignatureResult) => void` | — | Fires once when the component transitions to `complete` state (imageData present AND accepted === true). Use this as a submit-ready signal. |
| `methods` | `SignatureMethod[]` | `['draw', 'type', 'upload']` | Which capture methods to offer. Minimum one method required. If only one method is listed, the method-selector tabs are hidden and that method is used directly. |
| `defaultMethod` | `SignatureMethod` | `'draw'` | Initially active method. Must be a member of `methods`. |
| `acceptanceLabel` | `string` | `'I agree that this electronic signature is legally binding'` | Text label for the acceptance checkbox. Hosts may supply jurisdiction-specific wording. |
| `showSignerName` | `boolean` | `false` | Shows a text input for the signer's full name below the canvas area. |
| `showSignerTitle` | `boolean` | `false` | Shows a text input for the signer's title or role. |
| `showSignerDate` | `boolean` | `false` | Shows a date field pre-filled with today's date. |
| `signerDateEditable` | `boolean` | `false` | When `false` (default), the date field is read-only. When `true`, the signer may edit it. |
| `documentTitle` | `string` | — | If provided, renders a read-only context banner above the method tabs: "Signing: {documentTitle}". |
| `signerRole` | `string` | — | Shown in the context banner alongside `documentTitle`, e.g. "as Tenant". |
| `requiredFields` | `Array<'name' \| 'title' \| 'date'>` | — | Marks identity sub-fields as required. `name` and `title` require non-empty input; `date` requires a valid date. The component does NOT transition to `complete` until required fields pass. |
| `canvasHeight` | `number` | `160` | Forwarded to the `Signature` widget as `height`. |
| `signatureFormat` | `'svg' \| 'png'` | `'svg'` | Forwarded to `Signature` as `format`. Drives the `imageData` format in `SignatureResult`. |
| `typedFontFamily` | `string` | `'Dancing Script, Brush Script MT, cursive'` | CSS `font-family` applied to the typed-name preview rendering. |
| `disabled` | `boolean` | `false` | Disables all interactive sub-elements. Prevents drawing, typing, uploading, and toggling acceptance. |
| `error` | `boolean` | `false` | Enables error visual treatment on the root container. Does not set `aria-invalid` on the canvas (not form-submittable) — see Accessibility contract §4. |
| `className` | `string` | — | Additional CSS classes on the root wrapper. |

### 4.1 Controlled vs. uncontrolled

ESignatureField supports both modes via `value`:

- **Controlled (`value` provided):** The host owns the `SignatureResult`. The
  component reflects `value` on each render. The host must update `value` in
  response to `onChange`.
- **Uncontrolled (`value` omitted):** The component manages internal state.
  `onChange` and `onComplete` still fire. Use `defaultMethod` to set the
  initial tab.

Partial control (e.g., setting `value.method` while leaving `value.imageData`
empty) is not a supported use case. Hosts must always supply a complete
`SignatureResult` or omit `value` entirely.

### 4.2 `requiredFields` interaction with `complete` state

When `requiredFields` is supplied, the component enters `complete` state only
when ALL of the following hold:

1. `imageData` is non-empty (a capture has been made).
2. `accepted === true` (acceptance checkbox is checked).
3. Every field named in `requiredFields` has a valid value.

The Clear action resets all three conditions.

---

## 5. Events summary

| Event | Payload | Fired when |
|---|---|---|
| `onChange` | `SignatureResult` | Any change to method, canvas, typed name, upload, acceptance, or identity fields. |
| `onComplete` | `SignatureResult` | First transition to `complete` state (accepted + imageData + requiredFields satisfied). NOT re-fired on subsequent identical-state `onChange` calls. |

---

## 6. States

| State | Condition |
|---|---|
| `idle` | No capture has been made; acceptance unchecked. Initial state. |
| `signing` | The user is actively interacting: canvas has at least one stroke, or typed name is non-empty, or an upload has been selected — but `accepted === false` or `requiredFields` not yet satisfied. |
| `complete` | `imageData` non-empty AND `accepted === true` AND all `requiredFields` satisfied. `onComplete` has fired. |
| `cleared` | The user clicked Clear after reaching `complete`. Functionally equivalent to `idle` — the component is ready for a fresh capture. |

---

## 7. Composition with `FormFieldContext`

ESignatureField calls `useFormField()` and consumes:

| Context field | Used on |
|---|---|
| `id` | `aria-labelledby` on the method-tabs `role="tablist"` + root region |
| `describedBy` | `aria-describedby` on the root region (error/hint IDs) |
| `error` | Combined with the local `error` prop via logical OR |
| `disabled` | Combined with the local `disabled` prop via logical OR |
| `required` | Drives `aria-required` on the acceptance checkbox |

When used outside a `FormField`, the context returns inert defaults and the
component is fully self-contained with its own labels.

> **FR-1.4 supersession note (2026-06-11).** `FormFieldContextValue` did not
> previously define `required` or `disabled` fields — this section was a
> forward-spec referencing fields that did not yet exist on the context type.
> FormField.Semantic wave FR-1.4 (family-rulings-2026-06-11.md) adds both
> fields to `FormFieldContextValue` and documents the canonical logical-OR
> consumption rule. Once that wave ships: (1) any `describedBy`-substring
> heuristic used by this component to infer `required` state is retired in
> favour of `FormFieldContext.required`; (2) the implementation is
> unblocked to consume `context.required` and `context.disabled` directly
> with no further contract amendment needed.

---

## 8. Relationship to `Signature`

`ESignatureField` COMPOSES `Signature` for the `draw` method:

- `Signature` props forwarded: `height={canvasHeight}`, `format={signatureFormat}`,
  `disabled`, `onChange` (internal handler), `onClear` (internal handler).
- `Signature` props NOT forwarded: `value`, `defaultValue`, `className`,
  `strokeWidth`, `color`, `backgroundColor`, `exportScale`, `smooth`, `width`
  (ESignatureField controls the layout width).

For the `type` and `upload` methods, ESignatureField handles its own rendering
without delegating to `Signature`.

---

## 9. Deferred features

| Feature | Notes |
|---|---|
| Wet-ink animation for typed signature | Cursor-like blink on typed-name preview |
| Multi-page document context display | Showing page ranges or section names in the context banner |
| Signature font selection | Allowing the signer to choose from a set of cursive fonts |
| Remote audit trail submission | Posting the `SignatureResult` to a backend audit log directly from the component (this concern belongs in the host) |
| Biometric / identity verification integration | Outside the component's scope; host-level concern |
