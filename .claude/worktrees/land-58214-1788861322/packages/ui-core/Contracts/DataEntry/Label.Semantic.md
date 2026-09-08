# Label — Semantic Contract

- **Component:** Label
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Label.Interaction.md) · [Accessibility](./Label.Accessibility.md) · [Styling](./Label.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/` (forward-spec — no direct implementation; source: Radix @radix-ui/react-label)
- **Catalog row:** #74 Label (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 forward-spec (no reference implementation; Radix Label baseline)
- **Foundation:** Radix UI `@radix-ui/react-label`

---

## 1. Component purpose

**Label** — a thin wrapper around Radix `@radix-ui/react-label`. Provides an accessible `<label>` element with correct `htmlFor` association to form controls. Prevents label-click from accidentally triggering unrelated interactions (Radix handles this via the `onMouseDown` guard).

---

## 2. Props (planned)

```typescript
interface LabelProps extends React.LabelHTMLAttributes<HTMLLabelElement> {
  htmlFor?: string      // maps to native `for` attribute
  asChild?: boolean
  className?: string
  children?: React.ReactNode
}
```

All `HTMLLabelElement` attributes are spread onto the underlying `<label>`.

---

## 3. Relationship to FormField

`Label` is the standalone primitive. `FormField` wraps a Radix Label primitive directly — it does NOT compose this local `Label` component. Use this `Label` for custom field layouts outside FormField where you need the label styling without the full FormField context wiring.

---

## 4. Disabled styling

When a sibling or associated input is disabled, `Label` applies reduced opacity to communicate the disabled state. This is CSS-based via `peer-disabled:cursor-not-allowed peer-disabled:opacity-70`.

---

## 5. Wave-N expansion — editor-context prop set (2026-06-11)

**Audit source:** `_shared/design/polish/kendo-spec-audit/indicators-labels.md` (Label 50% coverage)
**Rulings applied:** FR-1 (validation contract), FR-3 (appearance axes — `editorValid` maps to `error` vocabulary)

### 5.1 Gaps addressed

| Gap (audit) | Priority | Resolution |
|---|---|---|
| `editorId` absent — explicit `for` association | P1 | `editorId` added — §5.2 |
| `editorValid` absent — validation state drives label styling | P1 | `editorValid` added; mapped to FR-1 `error` vocabulary — §5.3 |
| `editorDisabled` absent — label styling when control is disabled | P1 | `editorDisabled` added — §5.4 |
| `optional` prop absent — "(Optional)" suffix | P1 | `optional` added — §5.5 |
| `editorRef` absent — click redirect for non-native controls | P2 | `editorRef` added — §5.6 |
| `id` not explicitly specced | P2 | `id` noted as passthrough — §5.7 |

### 5.2 Updated data model

```typescript
interface LabelProps extends React.LabelHTMLAttributes<HTMLLabelElement> {
  // --- existing (kept) ---
  htmlFor?:  string                  // maps to native `for` attribute
  asChild?:  boolean
  className?: string
  children?: React.ReactNode

  // --- wave-N additions (editor-context prop set) ---

  /** Explicit association with the editor control. Maps to the native
   *  `<label for="...">` attribute. Kendo: `editorId`.
   *  Prefer `editorId` over `htmlFor` for Kendo-migration clarity; both
   *  map to the same DOM attribute — `editorId` takes precedence if both
   *  are provided (dev warning emitted). */
  editorId?: string

  /** Validation state of the associated editor. When `false`, the label
   *  renders in the error state (typically error-coloured text).
   *  Kendo: `editorValid` (false = invalid). FR-1 note: Kendo's `valid`
   *  inverts to our `error: boolean` convention. Here we accept Kendo's
   *  `editorValid` form (valid=false means error) for parity; internally
   *  the implementation maps: `labelError = editorValid === false`.
   *  See FR-1 §2 — validation messages stay composed outside Label. */
  editorValid?: boolean

  /** Disabled state of the associated editor. When `true`, the label
   *  applies disabled styling (reduced opacity, not-allowed cursor).
   *  Kendo: `editorDisabled`.
   *  This is the explicit-prop alternative to the current CSS-peer approach
   *  (`peer-disabled:…`). Both mechanisms are supported; `editorDisabled`
   *  takes precedence when present (deterministic; CSS peer requires the
   *  input to be a DOM sibling). */
  editorDisabled?: boolean

  /** Renders an "(Optional)" suffix after the label text. Default: false.
   *  Kendo: `optional`. The suffix string is localized via the i18n token
   *  `--sf-label-optional-text` (default: "Optional").
   *  Note: required fields are marked via FormField's `required` prop +
   *  the asterisk mechanism (FR-1 §1); `optional` is the complement for
   *  optional field indication patterns. */
  optional?: boolean

  /** Ref to the editor element for programmatic focus on label click for
   *  non-native controls (e.g., a custom Select or DatePicker that doesn't
   *  implement native focus-on-label-click).
   *  Kendo: `editorRef`.
   *  Radix Label's built-in `onMouseDown` guard already handles native
   *  `<input>` / `<select>` / `<textarea>`. `editorRef` is for non-Radix
   *  custom controls: Label calls `editorRef.current?.focus()` in its
   *  click handler when the ref is provided. */
  editorRef?: React.RefObject<HTMLElement>

  /** `id` on the label element itself — used when another element references
   *  this label via `aria-labelledby`. Passed through via HTML attribute
   *  spread (existing); explicitly listed here for documentation clarity.
   *  Kendo: `id`. */
  id?: string
}
```

### 5.3 editorValid → visual state mapping

`editorValid=false` triggers the label's error visual state:

| `editorValid` | CSS data attribute | Token applied |
|---|---|---|
| `true` or `undefined` | (none) | Default label colour |
| `false` | `data-invalid="true"` on root `<label>` | `--sf-label-error-color` |

PAO Styling maps `data-invalid="true"` to the label-specific error token. This is aligned with FR-1 §2 (we do not add a `valid` prop — `editorValid=false` means invalid; omission means valid).

### 5.4 editorDisabled behaviour

When `editorDisabled=true`:

- Root `<label>` receives `data-disabled="true"`.
- PAO Styling applies `--sf-label-disabled-opacity` (≈ 0.7) + `cursor: not-allowed`.
- This is the explicit-prop path; the CSS `peer-disabled:` path (current §4) remains as a fallback for cases where the editor is a sibling DOM element and `editorDisabled` is not provided.

### 5.5 optional suffix rendering

```html
<!-- When optional={true} -->
<label for="firstName">
  First name
  <span class="sf-label-optional" aria-hidden="true"> (Optional)</span>
</label>
```

- The suffix span is `aria-hidden="true"` — the accessible name of the label is the main text only.
- The string "(Optional)" is sourced from i18n token `--sf-label-optional-text` (default string, not a CSS token — this will be a component-level i18n string when the i18n wave lands; until then it is hardcoded to `"Optional"` in the implementation).
- `optional` and `required` (from FormFieldContext via FR-1) are mutually exclusive in practice; Label does not enforce this — the host must not set both.

### 5.6 editorRef click delegation

When `editorRef` is provided:

```tsx
// Internal handler added to the <label> onClick
const handleClick = (e: React.MouseEvent) => {
  if (editorRef?.current && e.target === e.currentTarget) {
    editorRef.current.focus()
    e.preventDefault()  // prevent native htmlFor focus (we're redirecting it)
  }
}
```

This is for non-native controls (e.g., a custom ComboBox built without a native `<input>`) that do not gain focus from the browser's native label-click behaviour. Native controls do NOT need `editorRef` — Radix's `onMouseDown` guard handles them.

### 5.7 `id` documentation note

`id` is a standard HTML attribute available via spread (`LabelHTMLAttributes`). It was not previously listed as a named prop. Wave-N explicitly documents it so downstream consumers (e.g., `FloatingLabel` §5.7) can reference it.

### 5.8 FormField vs Label prop-set interaction

`FormFieldContext` (FR-1 §4) gains `required` and `disabled`. When a `Label` is inside a `FormField`, the FormField SHOULD pass `editorDisabled` and drive validation state directly, making the `editorValid` / `editorDisabled` props on Label unnecessary in that context. Label's explicit props exist for **standalone usage outside FormField**.

### 5.9 Deferred from this wave

- **FR-1 `required` asterisk on Label** — standalone Label outside FormField does not yet render an asterisk. FormField handles it for composed usage. Standalone `required?` prop on Label deferred to FR-1 application wave.
- **editorRef focus delegation for Tauri/WebView environments** — focus behaviour differences deferred to platform wave.
