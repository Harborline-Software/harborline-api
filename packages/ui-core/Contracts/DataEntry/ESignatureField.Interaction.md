# ESignatureField — Interaction Contract

- **Component:** ESignatureField
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ESignatureField.Semantic.md) · [Interaction](./ESignatureField.Interaction.md) · [Accessibility](./ESignatureField.Accessibility.md) · [Styling](./ESignatureField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ESignatureField.tsx` (not yet implemented)
- **Catalog row:** #A20 ESignatureField (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation)

---

## 1. Scope

This contract covers the method selector (tab switching), the three capture
methods (draw / type / upload), the acceptance confirmation checkbox, the
signer identity fields, the Complete and Clear flows, and keyboard navigation.

---

## 2. Method selector — tab panel

ESignatureField presents up to three capture method tabs in a
`role="tablist"` + `role="tab"` + `role="tabpanel"` structure (see
Accessibility contract §2 for the full ARIA specification).

### 2.1 Switching methods

| Trigger | Behaviour |
|---|---|
| Click a method tab | Activates the selected tab panel. The previously-active panel hides. Any in-progress capture in the outgoing method is discarded. `onChange` fires with `imageData: ''` and `method` updated to the new selection. |
| Keyboard arrow keys (Left / Right) on a focused tab | Moves focus to the adjacent tab (wraps at ends). Does NOT activate the tab — user must press Enter or Space to confirm (see §2.2). |
| Enter or Space on a focused tab | Activates the focused tab (same as click). |

### 2.2 Activation vs. focus

The tab panel uses **manual activation** (focus moves independently of
selection). This matches the ARIA Authoring Practices Guide tabpanel pattern
and prevents accidental discard of an in-progress capture when the user
keyboards through the tabs.

### 2.3 Single-method degenerate case

When `methods` has exactly one entry, the tab list is not rendered. The single
panel is shown directly. No tab keyboard navigation applies.

---

## 3. Draw method

The Draw panel renders the `Signature` widget. All draw interaction is
delegated to the `Signature` component (see `Signature.Interaction.md`):

- Mouse and touch strokes update `imageData` on each stroke end.
- The `Signature` `onChange` callback triggers ESignatureField's own `onChange`
  with the updated `imageData` and `method: 'draw'`.
- When `disabled === true`, the Signature widget is put into disabled mode (no
  drawing).

The Draw panel also renders the method's own Clear button, which delegates
to `Signature`'s clear action and then fires ESignatureField's full clear
flow (§6).

---

## 4. Type method

### 4.1 Typed-name input

The Type panel renders a plain text input for the signer's name. As the user
types:

1. The input value is updated.
2. Below the input, a preview renders the typed text in the configured
   `typedFontFamily` at approximately 32px, styled to resemble a pen signature.
3. On each change, ESignatureField generates `imageData` from the preview
   rendering. The generation strategy is:
   - **Canvas-based (preferred):** render the text into an off-screen canvas
     using `ctx.font`, export as PNG data URL. **MUST `await document.fonts.load('32px Dancing Script')` before calling `ctx.fillText`** — if the font has not loaded, browsers silently fall back to a system font for the canvas render while the DOM preview (which uses CSS) shows the correct cursive font. The user sees a correct preview but the captured image uses a non-cursive font, and the discrepancy is invisible. If the font fails to load, fall back to the SVG strategy below.
   - **SVG-based (fallback):** produce an inline SVG `<text>` element with the
     font stack applied. Prefer this if font loading fails — SVG `<text>` respects CSS font loading and will render correctly once the font is available.
   The selected strategy is an implementation detail and need not be declared
   in props.
4. `onChange` fires with the updated `imageData` (PNG or SVG) and `method: 'type'`.

### 4.2 Empty input

When the typed input is empty, `imageData` is `''`. The component stays in
`idle` state regardless of the acceptance checkbox.

### 4.3 Typed-name and signer identity separation

If `showSignerName === true`, the identity section (§7) renders its own name
field. The typed-name input in the Type panel is SEPARATE from the signer
identity name — it is the signature capture itself, not identity metadata.
The host is responsible for deciding whether to pre-populate one from the
other.

---

## 5. Upload method

### 5.1 File selector

The Upload panel renders a drag-and-drop zone and a "Browse" button that
opens a file picker (`<input type="file">`). Accepted types:
`image/png, image/jpeg, image/webp, image/svg+xml`.

### 5.2 File validation

On file selection (drag-drop or browse):

1. Validate `file.type` against the accepted list. If invalid, show an inline
   error message ("File must be a PNG, JPEG, WebP, or SVG image") and leave
   `imageData` unchanged.
2. Read the file via `FileReader.readAsDataURL`. On `load`:
   - Set `imageData` to the data URL.
   - Fire `onChange` with `method: 'upload'` and the new `imageData`.
3. Render a thumbnail preview of the uploaded image (constrained to the panel
   width; max 160px tall).

### 5.3 Replace / remove

Once an image is uploaded, the zone shows the thumbnail plus a "Remove" button.
Clicking Remove clears `imageData` (fires `onChange` with `imageData: ''`),
removes the thumbnail, and returns the zone to its empty state.

### 5.4 Drag-and-drop

| Event | Behaviour |
|---|---|
| `dragover` | Calls `e.preventDefault()`. Applies drag-active visual treatment to the zone. |
| `dragleave` | Removes drag-active visual treatment. |
| `drop` | Calls `e.preventDefault()`. Extracts `e.dataTransfer.files[0]`. Proceeds through §5.2 validation. |

### 5.5 Disabled mode

When `disabled === true`, the file picker and drag-drop zone are inert. The
drop zone renders with `cursor-not-allowed opacity-50`. Keyboard focus on
the Browse button is blocked via `disabled` attribute.

---

## 6. Clear flow

Clear resets the component to the `cleared` state (functionally equivalent to
`idle`). Clear is available in all methods via a "Clear" or "Remove" button.

Order of operations:

1. Reset the active method's capture:
   - **draw:** delegates to `Signature`'s clear action (resets canvas, fires
     `Signature.onChange('')`).
   - **type:** empties the typed-name input.
   - **upload:** removes the uploaded file, empties the thumbnail.
2. Set `imageData = ''`.
3. If `accepted === true`, uncheck the acceptance checkbox (set
   `accepted = false`).
4. Fire `onChange` with a `SignatureResult` where `imageData: ''`,
   `accepted: false`, and all identity fields at their current values.

---

## 7. Signer identity fields

Identity fields are rendered below the capture area, above the acceptance
section. They appear only when their respective `show*` prop is `true`.

| Field | Input type | Default value | Editable |
|---|---|---|---|
| Full name | `<input type="text">` | `''` | Always editable |
| Title | `<input type="text">` | `''` | Always editable |
| Date | `<input type="date">` or formatted display | Today (ISO date) | Only when `signerDateEditable === true` |

Changes to identity fields fire `onChange` with the updated `signerName`,
`signerTitle`, or `timestamp` in the `SignatureResult`.

When a field is listed in `requiredFields` and is empty, the component
renders an inline validation message but does NOT prevent `onChange` from
firing. The `complete` state transition is blocked until the field is
satisfied (see Semantic contract §4.2).

---

## 8. Acceptance checkbox

The acceptance checkbox is always rendered at the bottom of the component,
below identity fields and above any host-provided error messaging.

| Trigger | Behaviour |
|---|---|
| Click or Space key | Toggles `accepted`. Fires `onChange` with the updated `accepted` value and current `imageData`. |
| Check when `imageData` non-empty and `requiredFields` satisfied | Transitions to `complete`. Fires `onComplete`. |
| Uncheck while in `complete` state | Transitions back to `signing`. Does NOT fire `onComplete`. |

When `disabled === true`, the checkbox is inert (`disabled` attribute set).

---

## 9. Complete state

When the component enters `complete`:

1. The method tabs become non-interactive (but remain visible and focusable for
   audit context). The tab panels are frozen — no further drawing, typing, or
   uploading is permitted.
2. A visual "Signature accepted" indicator appears (see Styling contract §7).
3. A "Clear and re-sign" button appears below the acceptance section.

### 9.1 Clear and re-sign

"Clear and re-sign" executes the Clear flow (§6) and then returns the component
to `idle` state with the originally active method still selected. Focus moves
to the first focusable element in the active panel.

---

## 10. Keyboard navigation summary

| Context | Key | Action |
|---|---|---|
| Tab list | Tab | Enter / leave the tab list. |
| Tab list | Left / Right Arrow | Move focus between tabs (no auto-activation). |
| Tab (focused) | Enter / Space | Activate the focused tab. |
| Type input | Tab | Move to next focusable element (acceptance checkbox or identity fields). |
| Upload Browse button | Enter / Space | Open file picker. |
| Upload zone | Tab | Navigate to Browse button. |
| Acceptance checkbox | Space | Toggle checked state. |
| Clear / Clear and re-sign button | Enter / Space | Execute clear flow. |
| Disabled component | Tab | Focusable elements receive focus but keyboard activation is blocked. |

Tab order within ESignatureField (when all sections are visible):

1. Method tabs (tablist)
2. Active panel content (canvas / type input / upload zone)
3. Identity fields (name → title → date, in order shown)
4. Acceptance checkbox
5. Clear / Clear and re-sign button (when applicable)

---

## 11. Known interaction gaps at forward-spec stage

| # | Gap | Notes |
|---|---|---|
| I1 | No undo / redo for draw strokes | Inherited from Signature component; see Signature.Interaction G4 |
| I2 | No signature font chooser for type method | Deferred; single font family |
| I3 | Paste from clipboard not supported for upload method | Common mobile pattern; deferred |
| I4 | Method switch discards in-progress capture without confirmation | Considered intentional for simplicity; may need a confirmation dialog for `complete` state |
