# FloatingLabel — Semantic Contract

- **Component:** FloatingLabel
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./FloatingLabel.Interaction.md) · [Accessibility](./FloatingLabel.Accessibility.md) · [Styling](./FloatingLabel.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FloatingLabel.tsx`
- **Catalog row:** #61 FloatingLabel (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** native `<label>` with CSS-positioned float animation

---

## 1. Component purpose

**FloatingLabel** — a layout wrapper that animates a label between placeholder position (unfocused, empty) and floating position (focused or filled). Wraps any single input-like child via `React.cloneElement`. The floating effect is achieved with CSS `:placeholder-shown` and peer classes.

---

## 2. Props

```typescript
interface FloatingLabelProps {
  label: string
  id?: string
  children: React.ReactElement  // single child input
  className?: string
}
```

---

## 3. cloneElement behavior

`FloatingLabel` clones its child and injects:
- `id`: uses `id` prop if provided, falls back to `child.props.id`
- `placeholder`: always `' '` (single space — required for `:placeholder-shown` CSS selector to work)
- `className`: merges `peer block w-full rounded-md border border-input bg-background px-3 pb-2 pt-5 text-sm focus:outline-none focus:ring-2 focus:ring-ring` with the child's existing className

---

## 4. Label association

`<label htmlFor={id ?? child.props.id}>` — explicit `for` association with the child input's id. Callers must ensure the child has an `id` prop or pass `id` directly to `FloatingLabel`.

---

## 5. Scope

FloatingLabel is a layout/animation wrapper. It does not add its own form state, validation, or fieldError handling. Combine with FormField for field-level error display.

---

## 6. Wave-N expansion — editor-context prop set + prop-driven float detection (2026-06-11)

**Audit source:** `_shared/design/polish/kendo-spec-audit/indicators-labels.md` (FloatingLabel 55% coverage)
**Rulings applied:** FR-1 (validation contract), FR-2 (focus event passthrough)

### 6.1 Gaps addressed

| Gap (audit) | Priority | Resolution |
|---|---|---|
| `editorValue` absent — controlled float-up trigger | P1 | `editorValue` added — §6.2 |
| `editorPlaceholder` absent — empty-state detection for non-input children | P1 | `editorPlaceholder` added — §6.3 |
| `editorDisabled` / `editorValid` absent | P1 | Both added — §6.4 |
| `optional` prop absent | P1 | `optional` added — §6.5 |
| `labelClassName` absent — inner label element styling | P2 | `labelClassName` added — §6.6 |
| `id` not listed as named prop | P2 | Explicit doc — §6.7 |

### 6.2 editorValue — controlled float detection

The current `:placeholder-shown` CSS approach works only when the child is a native `<input>` or `<textarea>` with a placeholder. It fails for non-input children (Select, DatePicker, custom controls) because they don't expose `:placeholder-shown`.

`editorValue` provides an explicit controlled signal for float state:

```typescript
/** The current value of the associated editor. Used to determine float state
 *  when the child is not a native input (does not support :placeholder-shown).
 *  Kendo: `editorValue`.
 *  Behaviour:
 *  - When `editorValue` is a non-empty string, the label is floated up.
 *  - When `editorValue` is '' (empty string) or undefined, CSS :placeholder-shown
 *    drives float (native-input path).
 *  - When `editorValue` is provided, it takes PRECEDENCE over :placeholder-shown
 *    (controlled wins over CSS heuristic).
 *  Typical usage: wrap a Select, DatePicker, or ComboBox whose value is
 *  controlled by the parent form state. */
editorValue?: string
```

Float state resolution logic:

```
isFloated =
  isFocused ||
  (editorValue !== undefined ? editorValue !== '' : !childPlaceholderShown)
```

Implementation note: when `editorValue` is provided, FloatingLabel applies a `data-floated="true"` attribute (or CSS class) rather than relying solely on `:placeholder-shown`. PAO Styling MUST provide a CSS rule targeting `[data-floated="true"] > label` alongside the `:focus-within > label` and `:has(input:not(:placeholder-shown)) > label` rules.

### 6.3 editorPlaceholder

```typescript
/** The placeholder text for the associated editor, used when the child
 *  does not natively expose :placeholder-shown (e.g., a custom Select).
 *  Kendo: `editorPlaceholder`.
 *  FloatingLabel uses this to determine empty state when `editorValue` is
 *  not provided: if the child visually shows `editorPlaceholder`, the
 *  field is considered empty and the label is not floated.
 *  Note: for native <input> children, `editorPlaceholder` is NOT needed —
 *  FloatingLabel already injects `placeholder=" "` (single space) for the
 *  CSS selector to work (§3 cloneElement behaviour). `editorPlaceholder`
 *  is for non-native children where cloneElement injection is not sufficient. */
editorPlaceholder?: string
```

### 6.4 editorDisabled + editorValid

Mirror the Label.Semantic.md §5.3 and §5.4 vocabulary:

```typescript
/** Disabled state of the associated editor. When `true`:
 *  - Label renders in disabled state (opacity + cursor).
 *  - FloatingLabel wrapper receives `data-disabled="true"`.
 *  - The label does not float on focus (disabled controls cannot receive focus).
 *  Kendo: `editorDisabled`. FR-1: maps to FR-1 disabled context. */
editorDisabled?: boolean

/** Validation state of the associated editor. When `false` (invalid):
 *  - Label renders in error state (error-coloured text).
 *  - FloatingLabel wrapper receives `data-invalid="true"`.
 *  Kendo: `editorValid` (false = invalid). FR-1: maps to `error: boolean`
 *  (editorValid === false → error state). */
editorValid?: boolean
```

`data-disabled` and `data-invalid` attributes on the FloatingLabel wrapper allow PAO Styling to target the inner label element via descendant selectors without prop drilling into the inner label.

### 6.5 optional suffix

```typescript
/** Renders an "(Optional)" suffix on the floating label. Default: false.
 *  Kendo: `optional`. Behaviour is identical to Label.Semantic.md §5.5.
 *  The suffix is visible in both the floating (raised) and placeholder
 *  (lowered) positions. */
optional?: boolean
```

### 6.6 labelClassName

```typescript
/** Additional className applied to the inner `<label>` element only
 *  (not the wrapper div). Useful for per-instance label typography or
 *  spacing adjustments.
 *  Kendo: `labelClassName`. The outer wrapper uses `className`. */
labelClassName?: string
```

DOM structure with wave-N additions:

```html
<div class="sf-floating-label-wrapper {className}"
     data-floated="{isFloated ? 'true' : undefined}"
     data-disabled="{editorDisabled ? 'true' : undefined}"
     data-invalid="{editorValid === false ? 'true' : undefined}">
  {clonedChild}
  <label for="{effectiveId}" class="sf-floating-label {labelClassName}">
    {label}
    {optional && <span class="sf-label-optional" aria-hidden="true"> (Optional)</span>}
  </label>
</div>
```

### 6.7 id documentation note

`id` is listed in the existing §2 props as `id?: string`. Wave-N confirms: `id` drives the `<label htmlFor>` and the `id` injected into the cloned child. The child's own `id` is used as fallback (existing §3 behaviour). Explicitly confirming the precedence: FloatingLabel `id` prop > child's `id` prop.

### 6.8 cloneElement deprecation path

The current `React.cloneElement` approach for injecting `id`, `placeholder`, and `className` into the child is a known fragility (breaks with wrapped children, React.forwardRef components that don't surface `id`, and strict-mode double-cloning). Wave-N does NOT fix this — it is deferred. The `editorValue` + `editorPlaceholder` additions reduce the footprint of what must be injected (no longer need `placeholder=" "` injection when `editorValue` is provided for non-input children). Full cloneElement → compound-component refactor is deferred to a subsequent wave.

### 6.9 FR-2 event passthrough (focus/blur)

Per FR-2, every interactive input adds `onFocus` and `onBlur` passthrough. FloatingLabel does NOT add its own `onFocus`/`onBlur` — it receives focus signals from its child via `:focus-within` CSS (current) and from the child's focus events propagating up the DOM tree. No additional event props are needed on FloatingLabel itself.

### 6.10 Deferred from this wave

- **cloneElement → compound-component refactor** — deferred (§6.8).
- **editorRef** — Label has it (§5.6); FloatingLabel defers — the child is already cloned/available so editorRef is redundant.
- **RTL label-float direction** — deferred to i18n wave.
- **Custom float transition duration** — PAO Styling token (`--sf-floating-label-transition-duration`); no per-instance override.
