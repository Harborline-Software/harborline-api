# Input — Semantic Contract

- **Component:** Input
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Input.Interaction.md) · [Accessibility](./Input.Accessibility.md) · [Styling](./Input.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Input.tsx`
- **Catalog row:** #72 Input (`app-priority: critical`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** native `<input>`

---

## 1. Component purpose

**Input** is the canonical low-level text-input primitive of `@harborline-software/ui-react`.
It is a thin, `forwardRef`-based wrapper around the native HTML `<input>`
element that adds the Harborline three-dimension styling vocabulary
(`size` / `fillMode` / `rounded`), an optional prefix / suffix slot pair, and an
`invalid` flag for error treatment.

Input is the **base primitive** that higher-level text inputs compose on:

- `TextBox` — adds value-typed `onChange(string)`, clear button, password reveal.
- `TextField` — adds `name`, controlled `value`/`onChange(string)`, FormField wiring (`aria-describedby`).
- `MaskedTextBox` — adds an input-mask transform layer.
- `AutoComplete` / `ComboBox` — embed Input as the editable text region.

Input itself is intentionally low-level: it does **not** normalize `onChange`
to `(value: string)`, does **not** read FormField context, does **not** wire
error messaging. Higher-level components add those concerns. Hosts that
consume Input directly accept the native `onChange(ChangeEvent)` signature.

---

## 2. Data model

Input has no internal state. It forwards the entire `React.InputHTMLAttributes`
surface to the underlying `<input>` element, with three Harborline-owned props
extracted: `size`, `prefix`, and `suffix`.

```typescript
type InputProps = Omit<React.InputHTMLAttributes<HTMLInputElement>, 'size' | 'prefix'> & {
  size?: 'small' | 'medium' | 'large'
  fillMode?: 'solid' | 'outline' | 'flat'
  rounded?: 'small' | 'medium' | 'large' | 'full'
  prefix?: React.ReactNode
  suffix?: React.ReactNode
  invalid?: boolean
}
```

The `Omit<…, 'size' | 'prefix'>` strips the two native attributes that Harborline
overrides — HTML `size` (display width in characters) and HTML `prefix`
(metadata attribute) — because those native semantics are incompatible with
the Harborline vocabulary.

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `size` | `'small' \| 'medium' \| 'large'` | `'medium'` | Vertical size. Maps to height + text size + horizontal padding (see §3.1). |
| `fillMode` | `'solid' \| 'outline' \| 'flat'` | `'solid'` | Background + border treatment. `solid` = white bg + border; `outline` = transparent bg + border; `flat` = transparent + bottom-border only (no rounding regardless of `rounded`). |
| `rounded` | `'small' \| 'medium' \| 'large' \| 'full'` | `'medium'` | Border radius. Ignored when `fillMode='flat'` (the underline treatment is intentionally square). |
| `prefix` | `ReactNode` | — | Optional adornment rendered before the input region. Activates the wrapped-container rendering mode (§4). |
| `suffix` | `ReactNode` | — | Optional adornment rendered after the input region. Activates the wrapped-container rendering mode (§4). |
| `invalid` | `boolean` | `false` | When `true`, renders the error border treatment (`border-destructive`). Does **not** set `aria-invalid` directly — see §3.4. |
| `className` | `string` | — | Composes with the Harborline base classes via `cn(...)`. Applied to the outermost rendered element (the `<input>` in plain mode, the wrapping `<div>` in adornment mode). |
| _native HTML_ | `InputHTMLAttributes` (minus `size`, `prefix`) | — | All other native input attributes are spread onto the underlying `<input>`: `type`, `value`, `defaultValue`, `onChange`, `onBlur`, `onFocus`, `placeholder`, `disabled`, `readOnly`, `required`, `autoComplete`, `name`, `id`, `aria-*`, `data-*`, etc. |

### 3.1 Size token mapping

| `size` | Height | Text size | Horizontal padding |
| --- | --- | --- | --- |
| `small` | `h-7` (28 px) | `text-sm` | `px-2` |
| `medium` | `h-9` (36 px) | `text-sm` | `px-3` |
| `large` | `h-11` (44 px) | `text-base` | `px-4` |

These are the **current Tailwind utility values**. PAO Styling owns the
eventual token-named replacements. The `px-*` value is consumed only by the
plain rendering mode; the adornment mode strips inline padding and pushes it
into the prefix/suffix `<span>` wrappers (see §4).

### 3.2 `fillMode` semantics

| `fillMode` | Background | Border | Rounding behaviour |
| --- | --- | --- | --- |
| `solid` | `bg-white` | `border border-input` | Honours `rounded` |
| `outline` | `bg-transparent` | `border border-input` | Honours `rounded` |
| `flat` | `bg-transparent` | `border-0 border-b border-input` | **`rounded-none` always** — the underline treatment cannot be rounded |

`flat` is the Material-style "underline-only" treatment. The interaction
between `flat` and `rounded` is **flat wins**: the implementation appends
`rounded-none` after the rounded class so `flat` is always square regardless
of the `rounded` value passed.

### 3.3 `type` — full native passthrough

Input does **not** restrict `type`. Every HTML5 input type is supported by
spreading through native attributes: `text`, `email`, `url`, `tel`,
`password`, `search`, `number`, `date`, `time`, `datetime-local`, `month`,
`week`, `color`, `file`, `hidden`, `range`, `checkbox`, `radio`, `submit`,
`reset`, `button`, `image`.

In practice Input is the substrate for **text-family** inputs only (`text`,
`email`, `url`, `tel`, `password`, `search`, `number`). Checkbox / radio /
button / file all have dedicated Harborline components and should not be
expressed via Input + `type="…"`.

The styling vocabulary (sizes, fillModes, prefix/suffix) assumes a text
input — passing `type="checkbox"` produces a visually broken control.
The contract does not enforce this; it is a host-discipline rule.

### 3.4 `invalid` and `aria-invalid`

The implementation sets `invalid` as a **styling-only** flag — it adds
`border-destructive` to the rendered element. It does **NOT** automatically
set `aria-invalid` on the underlying input.

Hosts that want the assistive-technology announcement must pass
`aria-invalid={true}` themselves via the native attribute passthrough:

```tsx
<Input invalid aria-invalid value={value} onChange={onChange} />
```

This is a deliberate separation: `invalid` is the visual contract,
`aria-invalid` is the a11y contract, and the two surfaces can disagree
during transient states (e.g. an input that is *being typed into* by a
user whose previous value was invalid — the visual treatment may still
be red while `aria-invalid` is `false` because the value is being
corrected). Higher-level components (TextField, FormField) reconcile
the two surfaces automatically.

See `Input.Accessibility.md` for the AT contract.

### 3.5 Controlled vs uncontrolled

Input does not enforce either mode. The host chooses:

- **Controlled** — pass `value` + `onChange`. React drives the input value
  on every render.
- **Uncontrolled** — pass `defaultValue` (and optionally a `ref` to read
  the current value). The DOM owns the value.

Switching between modes mid-lifetime (e.g. `value` going from `undefined`
to a `string`) emits the standard React warning and is unsupported.
This is native React behaviour; Input does not intercept.

### 3.6 `ref` forwarding

Input is wrapped in `React.forwardRef<HTMLInputElement, InputProps>`. The
`ref` attaches to the underlying `<input>` element in **both** rendering
modes (plain and wrapped). Hosts can call `inputRef.current?.focus()`,
read `inputRef.current?.validity`, or attach IME handlers directly to
the DOM node.

---

## 4. Rendering modes

Input has two rendering modes selected at render time based on whether
`prefix` or `suffix` is set:

### 4.1 Plain mode — neither `prefix` nor `suffix`

```html
<input class="<sizes + fillMode + rounded + invalid + className>" ... />
```

A single `<input>` element with all classes applied directly. The
horizontal padding (`px-2` / `px-3` / `px-4`) lives on the input itself.

### 4.2 Adornment mode — `prefix` or `suffix` is non-null

```html
<div class="flex items-center <fillMode + rounded + size-without-px + invalid + className>">
  <span class="px-2 text-muted-foreground shrink-0">{prefix}</span>
  <input class="flex-1 bg-transparent outline-none min-w-0 [pl-3 if no prefix] [pr-3 if no suffix]" ... />
  <span class="px-2 text-muted-foreground shrink-0">{suffix}</span>
</div>
```

Behavioural notes:

- The size's horizontal padding (`px-N`) is stripped from the wrapper and
  re-applied to the `<span>` adornments. The input itself takes
  `pl-3` / `pr-3` only on the **opposite** side of the missing adornment.
- The wrapper carries a `focus-within:ring-2 focus-within:ring-ring`
  treatment so the entire row appears focused when the input is focused —
  the input itself has `outline-none` to avoid a double ring.
- `invalid` applies to the wrapper (`border-destructive`), not the input.
- The adornment spans have `text-muted-foreground` by default; hosts can
  override by passing styled `ReactNode` content.

### 4.3 Switching between modes

Adding or removing `prefix`/`suffix` re-renders Input in the other mode.
React preserves the input DOM node only if it is at a matching position
in the tree; in practice mode-switching does **not** preserve the input
node (the wrapper insertion changes the tree shape). Hosts that hold a
`ref` should treat mid-lifetime mode switches as a node replacement —
the ref will repoint, but focus / selection / IME state will be lost.

Recommendation: pick a mode at mount and keep it stable. If adornments
appear conditionally, render an empty `<span/>` as the placeholder slot
to keep the tree shape constant.

---

## 5. Events — semantics

Input does not own any events. It forwards every native event handler
that the host passes — `onChange`, `onBlur`, `onFocus`, `onKeyDown`,
`onKeyUp`, `onInput`, `onCompositionStart` / `End`, `onPaste`, etc.

| Event | Native payload | Notes |
| --- | --- | --- |
| `onChange` | `ChangeEvent<HTMLInputElement>` | React synthetic — fires per keystroke. Payload is `e.target.value`. |
| `onBlur` | `FocusEvent<HTMLInputElement>` | Standard React focus event. |
| `onFocus` | `FocusEvent<HTMLInputElement>` | Standard React focus event. |
| `onKeyDown` | `KeyboardEvent<HTMLInputElement>` | Use for Enter-to-commit patterns at higher levels. |

Higher-level wrappers (`TextBox`, `TextField`) normalize `onChange` to
`(value: string) => void`. Input itself preserves the native signature.

---

## 6. Slots

| Slot | Purpose | Container |
| --- | --- | --- |
| `prefix` | Adornment before the input region — icon, currency symbol, "@", unit prefix. | `<span class="px-2 text-muted-foreground shrink-0">` |
| `suffix` | Adornment after the input region — unit suffix, clear button, password reveal toggle, validation indicator. | `<span class="px-2 text-muted-foreground shrink-0">` |

The slots accept arbitrary `ReactNode`. Common content:
- Icon components (`<MailIcon />`, `<SearchIcon />`).
- Currency symbols (`"$"`).
- Interactive buttons (`<button>` for clear / reveal / search-submit) —
  the host owns the `tabIndex={-1}` discipline if the affordance should
  not be in the focus order.

Slots are **not** named — they are simple `ReactNode` props. No header /
footer / leading-trailing-aligned slots. Multiple adornments per side are
expressed as a single Fragment (`<><X /><Y /></>`).

---

## 7. Component composition

- **TextBox builds on Input.** TextBox extends `InputProps` with a value-typed
  `onChange(string)` and adds clear-button / password-reveal affordances into
  the suffix slot. See `TextBox.Semantic.md`.
- **TextField builds on the same native `<input>` pattern** but does not
  consume Input directly — TextField has its own native `<input>` render path
  to integrate with FormField context (`useFormField()` describedBy wiring).
  This is a legacy divergence; future unification may have TextField wrap
  Input. (Open question §8.3.)
- **AutoComplete / ComboBox** use Input as the editable text region.
- **MaskedTextBox** wraps Input and intercepts onChange for mask-transform.
- **Form integration.** Input renders a native `<input>` — it participates
  in native form submission via its `name` attribute.

---

## 8. Open questions (for council)

1. **`invalid` vs `error` naming.** Input uses `invalid`; TextField uses
   `error`. Same concept, different name. Should the family align on one
   term? (Leaning `invalid` — matches native `aria-invalid` and HTML5
   `:invalid` pseudo-class.)
2. **Auto-set `aria-invalid` from `invalid`.** Today the visual flag and
   the a11y attribute are independent (§3.4). Should `invalid={true}`
   automatically set `aria-invalid="true"`? (Leaning yes, with an escape
   hatch `aria-invalid={false}` override; PAO Accessibility owns the
   final ruling.)
3. **TextField → Input unification.** TextField duplicates the
   native-input rendering. Should TextField wrap Input instead?
4. **Adornment-mode focus ring.** Currently the wrapper carries
   `focus-within:ring-2` and the input has `outline-none`. Should we
   expose the focus-ring offset / color as a styling token here, or
   defer entirely to PAO Styling?
5. **`type` restriction.** Should Input restrict `type` to text-family
   values (TypeScript union) to prevent `type="checkbox"` accidents?
   (Leaning no — Input is a low-level primitive; trust the host.)

---

## 9. Deferred features

- **Polymorphic `as` prop** — Input is always `<input>`. No `<textarea>` or
  `<select>` polymorphism (those are separate components).
- **Built-in clear button** — owned by TextBox.
- **Built-in password reveal** — owned by TextBox.
- **Built-in input-mask layer** — owned by MaskedTextBox.
- **Floating-label / "Material" label treatment** — out of scope; hosts
  use FormField for labels.
- **Inline character-count adornment** — host-rolled via the `suffix` slot.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/forms/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |

---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-IN1 | High | `invalid` does not automatically set `aria-invalid` | [ACCEPTED-RISK 2026-06-06] §3.4 documents the separation; PAO Accessibility may add auto-wiring in a fast-follow |
| G-IN2 | High | `invalid` vs `error` naming divergence with TextField | [OPEN 2026-06-06] §8.1 council question — family-wide vocabulary pin needed |
| G-IN3 | Medium | Mode-switch (plain ↔ adornment) drops input node identity | [ACCEPTED-RISK 2026-06-06] §4.3 documents host discipline (stable mode at mount) |
| G-IN4 | Medium | `type` is unconstrained — `type="checkbox"` produces a broken control | [ACCEPTED-RISK 2026-06-06] §3.3 documents text-family-only usage; not enforced by types |
| G-IN5 | Medium | `flat` + `rounded` interaction is silent override | [RESOLVED 2026-06-06] §3.2 documents `flat` wins → always `rounded-none` |
| G-IN6 | Low | No first-class adornment alignment / divider treatment | [ACCEPTED-RISK 2026-06-06] hosts compose with Fragment; future amendment may add structured adornment slots |
| G-IN7 | Low | TextField duplicates Input's native render path | [OPEN 2026-06-06] §8.3 — TextField → Input unification under consideration |
| G-IN8 | Medium | Size vocab divergence — Input uses `'small'/'medium'/'large'`; TextField uses `'sm'/'md'/'lg'`; TextArea uses `'small'/'medium'/'large'` | [OPEN 2026-06-06] OO3-19 — family-wide size token unification needed; council required; Fix-deferred M2 |
