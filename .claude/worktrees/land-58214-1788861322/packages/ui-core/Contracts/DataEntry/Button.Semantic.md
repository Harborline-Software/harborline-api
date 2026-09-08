# Button — Semantic Contract

- **Component:** Button
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Button.Interaction.md) · [Styling](./Button.Styling.md) · [Accessibility](./Button.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/buttons/Button.tsx`
- **Catalog row:** #17 Button (`app-priority: critical`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** shadcn/ui Button → Radix `Slot` + native `<button>`

---

## 1. Purpose

Button is the canonical action-trigger primitive of `@harborline-software/ui-react`. It is
the single entry point for "user activates an action" across the entire design
system — submit a form, open a dialog, navigate, run a side effect, etc.

This contract documents the **shipping** Button at
`packages/ui-react/src/components/buttons/Button.tsx`. The implementation
follows the shadcn/ui Button pattern (Radix `Slot` + native `<button>`)
adapted to Harborline's variant + size vocabulary. Per ADR 0017-A1 Phase M1,
this is a wave-N extraction from the shipping implementation; the contract
captures the contract-of-record for the existing component, not an
aspirational forward-spec.

Button is a **leaf primitive** — it is consumed directly by host code and
indirectly by every higher-level component that needs an action affordance
(Dialog footers, EmptyState CTAs, DataGrid bulk-actions, ConfirmDialog
buttons, AppBar action slots, etc.). Its contract is therefore load-bearing
for every other contract that references "a button".

---

## 2. Data model

Button has no internal data model. It is a presentational wrapper around a
native `<button>` (or any element via `asChild`, per shadcn convention) that
fires `onClick` when activated.

```typescript
type ButtonVariant = 'primary' | 'secondary' | 'tertiary' | 'destructive' | 'ghost'
type ButtonSize = 'sm' | 'md' | 'lg' | 'icon'
type ButtonType = 'button' | 'submit' | 'reset'

interface ButtonProps {
  // Behaviour
  variant?: ButtonVariant
  size?: ButtonSize
  type?: ButtonType

  // State
  disabled?: boolean
  loading?: boolean

  // Composition
  asChild?: boolean
  leadingIcon?: React.ReactNode
  trailingIcon?: React.ReactNode

  // Standard handlers + content
  onClick?: (e: React.MouseEvent<HTMLButtonElement>) => void
  children: React.ReactNode
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `variant` | `'primary' \| 'secondary' \| 'tertiary' \| 'destructive' \| 'ghost'` | `'secondary'` | Visual + semantic role (see §3.2). Drives the token surface owned by PAO Styling. |
| `size` | `'sm' \| 'md' \| 'lg' \| 'icon'` | `'md'` | Vertical size. `'icon'` is a square treatment for icon-only buttons (no `children` text). |
| `type` | `'button' \| 'submit' \| 'reset'` | `'button'` | Native button type. **Default is `'button'`**, not the browser native `'submit'`, to avoid accidental form submission when Button is dropped into a `<form>`. Hosts who want a submit button must opt in with `type="submit"`. |
| `disabled` | `boolean` | `false` | When `true`, the button is non-interactive (`disabled` attribute set), no `onClick` fires. Visual treatment owned by PAO Styling. |
| `loading` | `boolean` | `false` | When `true`, the button shows a leading spinner, `children` content remains visible (per shadcn convention) but the button is `disabled` for input. `onClick` does not fire while `loading`. |
| `asChild` | `boolean` | `false` | shadcn/Radix Slot pattern: when `true`, Button does not render its own `<button>`; instead it composes its styling + behaviour onto its single `children` element (typically a Next.js `<Link>` or `<a>`). See §3.3. |
| `leadingIcon` | `ReactNode` | — | Optional icon rendered before the text content. Replaced by the spinner when `loading === true`. |
| `trailingIcon` | `ReactNode` | — | Optional icon rendered after the text content. |
| `onClick` | `(e: MouseEvent) => void` | — | Activation callback (§4). Native event is forwarded so hosts can `preventDefault` / `stopPropagation` if needed. |
| `children` | `ReactNode` | _required_ | Button content (typically text, or text + adornments). For `size: 'icon'`, `children` is the icon itself; `leadingIcon`/`trailingIcon` are unused. |

### 3.1 Default `type` — note for Telerik users (closes G-BT4)

Telerik's `TelerikButton` also renders `<button type="button">` by default
(not the HTML-native `type="submit"`). Migrating from Telerik's Button to
Harborline Button produces **no behavioral change** for the `type` prop —
both default to `'button'`.

**Note for hosts replacing raw `<button>` elements:** the HTML specification
default is `type="submit"` (not `'button'`). If your codebase has raw
`<button>` elements inside `<form>` tags that relied on the browser's
submit default, you must add `type="submit"` explicitly when switching to
Harborline Button.

### 3.2 Variant semantics

| Variant | Intent | Typical use |
| --- | --- | --- |
| `primary` | The single canonical "go" action of the surface. | Form submit, primary CTA in a dialog footer, "Save" in an editor toolbar. At most one primary per surface (visual rule, not enforced). |
| `secondary` | Standard, non-decorative action. The default treatment. | Most buttons in the app — non-primary actions that still need a button affordance. |
| `destructive` | Action with irreversible or data-loss consequences. | "Delete", "Archive", "Cancel subscription". Often paired with a ConfirmDialog. |
| `tertiary` | Low-emphasis labelled action with minimal visual chrome. | Inline actions in dense content (breadcrumbs, form sub-actions) that need a distinct third tier below `secondary`. Uses `asChild` + `<a>`/`<Link>` for actual link navigation. |
| `ghost` | Low-emphasis action — no fill, no border at rest. | Toolbar actions, header actions, secondary-row actions where chrome would distract. |

#### 3.2.1 Variant model — note for Telerik users (closes G-BT1)

Telerik's Button separates two orthogonal axes: **ThemeColor** (Primary /
Secondary / Tertiary / Info / Success / Warning / Error / Base / Dark /
Light) and **FillMode** (Solid / Flat / Outline / Link / Clear). Harborline
`variant` **deliberately conflates** the two axes into a single vocabulary
because the Telerik cross-product (e.g. "Tertiary + Flat + Warning") is
rarely used in practice and the combinatorial surface is complex to token.

Mapping:
- Telerik `Primary + Solid` → Harborline `primary`
- Telerik `Base + Flat` → Harborline `ghost` (fill-mode concept)
- Telerik `Base + Outline` → Harborline `secondary`
- Telerik `Error + Solid` → Harborline `destructive` (theme-color concept)

**Consequence:** Hosts cannot express "a tertiary button with a
destructive color" — there is no `variant="tertiary-destructive"`.
For cross-product treatments pass `className` overrides. PAO Styling
owns the token surface.

### 3.3 `asChild` composition (Radix `Slot` pattern)

When `asChild === true`, Button does **not** render its own `<button>`
element. Instead it uses Radix's `Slot` primitive to forward its className,
event handlers, and disabled state onto its single `children` element. This
is the shadcn-canonical pattern for "I want this to look like a button but
actually be a link":

```tsx
<Button asChild variant="primary">
  <Link href="/properties/new">New property</Link>
</Button>
```

The host is responsible for ensuring `children` is a **single element** that
can accept arbitrary HTML attributes. Multiple children, or a fragment, will
fail at runtime.

When `asChild === true`, the `type` prop is **ignored** — the rendered
element controls its own type semantics.

### 3.4 `loading` state

When `loading === true`:

- A spinner replaces `leadingIcon` (or is inserted before `children` when no
  `leadingIcon` is supplied).
- `disabled` is internally treated as `true` — clicks do not fire `onClick`.
- The visible `children` text is **not** replaced (per shadcn convention; the
  user still sees what they were doing).

`loading` and `disabled` are independent props. Both can be `true`
simultaneously (e.g. "disabled while waiting for permission check"); the
visual treatment composes (PAO Styling owns the resolution).

**`loading` + `asChild` is unsupported (closes G-BT3).** When `asChild === true`,
the Button renders onto its child element (e.g. a Next.js `<Link>`). That element
does not have a native `disabled` attribute that blocks clicks the way a
`<button disabled>` does. Setting `loading={true}` on an `asChild` Button does
NOT prevent navigation or interaction on `<a>` / `<Link>` children — the Slot
forwards `className` (which includes the opacity + spinner styles) but cannot
prevent the child's default behavior. Hosts who need a loading-state navigation
link must handle the prevention themselves (e.g. `e.preventDefault()` in
`onClick` while loading). Do not rely on `loading` to "disable" an `asChild`
link during async operations.

### 3.5 HTML attribute passthrough

Button **spreads** any additional valid HTML attributes onto the rendered
`<button>` (or onto the `asChild` target via `Slot`). This includes but is
not limited to:

- `id`, `data-*`, `aria-*`
- `name`, `value` (when used inside a form)
- `form`, `formAction`, `formMethod`, `formNoValidate`, `formTarget`
- `autoFocus`, `tabIndex`

This passthrough is **already implemented**: `ButtonProps` extends
`React.ButtonHTMLAttributes<HTMLButtonElement>` and the implementation
spreads `...props` onto the rendered element (or the `asChild` target via
`Slot`). Other M1 components mostly lack attribute passthrough (documented
as a known gap); the contract amendment that retrofits passthrough across
those M1 components will use Button's spec + implementation as the
template.

### 3.6 Icon-only mode — `size='icon'` (closes G-BT2)

`size='icon'` is **structurally different** from text-button sizes (`sm` /
`md` / `lg`). It is NOT just a smaller button — it fundamentally changes
which props are active:

| Mode | Active props | Ignored props |
| --- | --- | --- |
| Text-button (`sm` / `md` / `lg`) | `children` (text), `leadingIcon`, `trailingIcon` | — |
| Icon-only (`icon`) | `children` (the icon itself) | `leadingIcon`, `trailingIcon` |

```tsx
// Correct — icon-only:
<Button size="icon" aria-label="Delete">
  <Trash2Icon className="h-4 w-4" />
</Button>

// WRONG — leadingIcon is silently ignored in icon mode:
<Button size="icon" leadingIcon={<Trash2Icon />}>
  Delete
</Button>
```

**`aria-label` is mandatory on icon-only buttons** — there is no visible
text to announce. A dev-mode warning for missing `aria-label` on
`size='icon'` buttons is planned (PAO Accessibility; not yet enforced).

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onClick` | `MouseEvent<HTMLButtonElement>` | The user activates the button — click, or Enter/Space when focused (native `<button>` behaviour). **Not fired** when `disabled === true` or `loading === true`. |

Button does not expose `onFocus` / `onBlur` / `onKeyDown` as named props in
M2; hosts route those via the HTML-attribute-passthrough surface (§3.5).

---

## 5. Slots

Button's slot model is small and prop-driven:

| Slot prop | Purpose |
| --- | --- |
| `leadingIcon` | Icon before text content. Replaced by spinner when `loading`. |
| `trailingIcon` | Icon after text content. Unaffected by `loading`. |
| `children` | The button label / content. For `size: 'icon'`, this is the icon itself. |

Named slots (header / footer / etc.) do not apply — Button is a single-region
component.

---

## 6. Component composition

- **Dialog footers, EmptyState CTAs, ConfirmDialog actions.** All consume
  Button directly. Their contracts reference Button rather than restating
  variant/size choices.
- **DataGrid bulk-actions.** Hosts pass `<Button>` content into the
  `bulkActions` slot.
- **Form integration.** A `<Button type="submit">` inside a `<form>`
  submits the form natively; no React-specific wiring required.
- **Icon-only buttons.** Use `size="icon"` plus an icon as `children`. Hosts
  MUST supply an `aria-label` (HTML attribute passthrough) for accessibility
  — owned by PAO Accessibility.
- **Button groups.** A future `ButtonGroup` component (catalog #18) will
  compose buttons with shared sizing and border-handling. Out of scope
  for M2.

---

## 7. Deferred features

Out of scope for the M2 baseline:

- **`link` variant** — the prior draft used `link` as a variant for inline text-styled affordances. Renamed to `tertiary` (council reconciliation) which better describes semantic weight; `asChild` + `<a>`/`<Link>` is the correct pattern for actual hyperlinks.
- **Outline-only treatment as a distinct variant** — current `secondary`
  spec leaves the bordered-vs-fill decision to PAO Styling tokens.
- **Color-prop overrides** (e.g. `color="success"`) — variants are
  semantic-only; PAO Styling controls hue.
- **Loading text override** (`loadingText?: string` to swap `children`
  while loading). The shadcn convention keeps children visible; this is
  deliberate.
- **Pending-async `onClick`** auto-loading — hosts must drive `loading`
  state explicitly.
- **`split-button` composition** — combined primary action + dropdown.
  Future SplitButton component.
- **Polymorphic `as` prop** beyond `asChild` — `asChild` covers the link
  case; arbitrary element types are not supported.

---

## 8. Open questions (for council)

1. **Default variant.** Implementation defaults to `'secondary'`. Open: should this match shadcn's `'primary'` default? **Status:** shipping with `'secondary'`; council may revisit, but accidental primaries proliferate when `'primary'` is the default.
2. **Default `type`.** Implementation defaults to `'button'` (safe default, matches Telerik). **Status:** shipping; deliberate divergence from HTML-native `'submit'` to avoid form-submit footguns.
3. **`loading` behaviour — replace text or keep text?** Implementation keeps `children` visible (shadcn convention) and replaces `leadingIcon` with the spinner. **Status:** shipping. Optional `loadingText` / `loadingChildren` slot is parked under §7 deferred features.
4. **`asChild` and `type`.** Implementation silently ignores `type` when `asChild === true` (`...(!asChild ? { type } : {})`). **Status:** shipping; matches Radix Slot semantics — the rendered element wins. No runtime warning.
5. **Icon-button accessibility enforcement.** Implementation does NOT warn at runtime when `size === 'icon'` lacks `aria-label`. **Status:** PAO Accessibility may add a dev-mode runtime check; currently a contract-only convention (see G-BT7).
6. **HTML attribute passthrough on M1 retrofit.** Button itself ships with passthrough (extends `ButtonHTMLAttributes`). **Status:** the M1-retrofit amendment for TextField / DataGrid / EmptyState / Pager remains open; Button is the template.

---

## Definition of Done

| Gate | Criterion |
|---|---|
| `demo-story: ✓` | Storybook story present; renders at 1280×800 without console errors; `A11y Storybook test-runner` CI passes |
| `visual-test: ✓` | Baseline PNG committed to `src/components/buttons/__screenshots__/`; `Visual regression (screenshot diff)` CI passes (≤1% pixel diff) |


---

## 15. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-BT1 | Critical | Variant↔FillMode conflation not named | [RESOLVED 2026-06-05] §3.1: variant conflates ThemeColor+FillMode; cross-product not supported |
| G-BT2 | Critical | `size="icon"` structural difference not called out | [RESOLVED 2026-06-05] §3 callout: icon mode uses children only; leadingIcon/trailingIcon unused |
| G-BT3 | High | `loading` + `asChild` interaction undefined | [RESOLVED 2026-06-05] §3.2 note: asChild+loading applies pointer-events:none via Slot className |
| G-BT4 | High | `type="button"` default diverges from HTML | [RESOLVED 2026-06-05] §3 + §8.2: default "button" is deliberate; migration note for Telerik users added |
| G-BT5 | Medium | loadingText deferred | [ACCEPTED-RISK 2026-06-05] §7 forward-pointer: loadingChildren slot is planned fast-follow |
| G-BT6 | Medium | Rounded/border-radius treatment absent | [ACCEPTED-RISK 2026-06-05] Radius implicit per variant/size; pill/square via className |
| G-BT7 | Medium | Icon-only aria-label enforcement risk | [ACCEPTED-RISK 2026-06-05] PAO Accessibility MAY add dev-mode runtime check |
| G-BT8 | Low | Companion contracts (Interaction / Accessibility / Styling) carry stale `Phase: M2 (forward-spec)` headers + "_not yet built_" reference-implementation lines | [RESOLVED 2026-06-06] DA3-11 sweep: Phase headers rewritten to `M1 (wave-N extraction)`; reference-implementation paths repointed to `packages/ui-react/src/components/buttons/Button.tsx`; body language updated to drop "forward-spec" framing |
