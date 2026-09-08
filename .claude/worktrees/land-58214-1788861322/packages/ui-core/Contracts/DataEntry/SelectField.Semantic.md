# SelectField — Semantic Contract

- **Component:** SelectField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./SelectField.Interaction.md) · [Styling](./SelectField.Styling.md) · [Accessibility](./SelectField.Accessibility.md)
- **Related contract:** [FormField.Semantic.md](./FormField.Semantic.md) — typical wrapper for label + hint + error.
- **Reference implementation:** `packages/ui-react/src/components/forms/SelectField.tsx`
- **Catalog row:** #48 DropDownList (`app-priority: critical`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** Radix UI `@radix-ui/react-select`

---

## 1. Purpose

SelectField is the canonical single-select dropdown of `@harborline-software/ui-react`. It
is a controlled component built on Radix UI's `@radix-ui/react-select`
primitive — chosen for its accessible-by-default keyboard interaction,
portalled popover (escapes overflow:hidden ancestors), and ARIA combobox
pattern compliance.

The contract treats the Radix primitive as an implementation detail; the
public surface is the props and events documented here.

---

## 2. Data model

SelectField is fully controlled — the host owns `value` and receives the next
value via `onValueChange(string)`. Options are passed as a flat array of
`{ value, label }` pairs.

```typescript
interface SelectOption {
  value: string
  label: string
}

interface SelectFieldProps {
  name: string
  value: string
  onValueChange: (v: string) => void
  options: SelectOption[]
  placeholder?: string
  disabled?: boolean
  error?: boolean
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `name` | `string` | _required_ | Field name. Drives the `id` and `name` attributes on the Radix `<Select.Trigger>`. Must match the parent FormField's `name`. |
| `value` | `string` | _required_ | Controlled selected value. Must be one of the `options[].value` strings, or the empty string for "no selection". |
| `onValueChange` | `(v: string) => void` | _required_ | Fired when the user selects an option. Payload is the new `option.value`. |
| `options` | `SelectOption[]` | _required_ | The list of selectable options. Order is the visual order in the popover. |
| `placeholder` | `string` | `'Select…'` | Trigger placeholder text when `value === ''`. Rendered in muted styling (`text-gray-400`). |
| `disabled` | `boolean` | `false` | Disables the trigger + popover. |
| `error` | `boolean` | `false` | Switches the trigger into the error treatment + sets `aria-invalid={true}`. |

### 3.1 Single-select baseline

SelectField is **single-select only** in M1. Multi-select is a separate
component (catalog has a future MultiSelect entry) and is out of M1 scope.

### 3.1.1 `value=''` as the "no selection" sentinel (closes G-SF3)

SelectField's `value` prop is `string`. The empty string (`""`) is the
canonical "nothing selected" state — Radix renders the `placeholder` text
when `value === ""`.

**Note for Telerik users and typed-value hosts:** Telerik uses `null` (for
nullable types) or `0` (for integers) as "no value". Harborline uses `""`.
**Hosts whose underlying value is numeric must serialize at the
SelectField boundary:**

```tsx
// host state: number | null
const [vendorId, setVendorId] = useState<number | null>(null)

<SelectField
  value={vendorId === null ? '' : String(vendorId)}
  onValueChange={(v) => setVendorId(v === '' ? null : Number(v))}
  options={vendors.map((v) => ({ value: String(v.id), label: v.name }))}
/>
```

This `string ↔ number` bridge is **host-owned**. SelectField has no
built-in `valueAsNumber` or `nullable` mode in M1.

### 3.1.2 Keyboard typeahead (closes G-SF4)

**Radix Select provides partial typeahead for free.** When the popover is
open, typing characters scrolls/highlights the first matching option whose
label starts with the typed prefix. This is a Radix-native behavior — not
documented in Harborline's forward-spec but available to all hosts.

The typeahead is:
- **Match-from-start only** (not substring).
- **Case-insensitive**.
- **Resets after ~1s of no input** (Radix's typeahead timer).
- **Not a filter** — the list does not shrink; non-matching items remain visible.

Hosts with large option lists (50+ options) should note that typeahead is
**not a replacement for a searchable dropdown** — it just highlights the
first prefix match. Full filtering is deferred (§7 "searchable / typeahead
filter").

### 3.2 Option model

`SelectOption` is flat in M1 — no `disabled` per-option, no grouping, no
icons, no descriptions. All deferred (§7).

### 3.3 Popover positioning

The Radix `<Select.Content>` uses `position="popper"` with `sideOffset={4}`.
The popover width is bound to the trigger width via
`min-w-[var(--radix-select-trigger-width)]`. Tokens / spacing for the popover
are owned by PAO Styling.

### 3.4 HTML attribute passthrough

Not implemented on the trigger or content. Known gap.

### 3.5 FormField integration

Same pattern as TextField — reads `describedBy` from `useFormField()` and
threads it onto `aria-describedby` on the trigger.

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onValueChange` | `(v: string)` | The user activates an option in the popover (click or Enter on the highlighted item). Radix's own `value` change. No separate "popover open" / "popover close" callbacks in M1. |

### 4.1 Popover lifecycle callbacks

**`onOpenChange` / `onOpen` / `onClose` are not exposed in M1** — the Radix
Select popover opens and closes silently with no host callback. Hosts that need
to lazy-load options on popover open have no event hook; the workaround is to
pre-load options eagerly into state before rendering the SelectField. See §7
(`onOpenChange` deferred).

**Async option loading is not supported in M1.** The `options` array is
synchronous and flat. There is no `onRead` callback (Telerik's equivalent for
virtualized/lazy data). Hosts with large or remote option lists must manage the
full array in state and update it themselves on mount (or on a separate trigger
outside the SelectField). See §7 (async/virtualised options deferred).

---

## 5. Slots

No slot extensibility in M1. Custom option content (icons, two-line items,
descriptions) is deferred (§7).

The popover renders a `Select.ItemIndicator` (a `Check` icon) next to the
currently selected option — this is a fixed treatment, not slot-customisable.

---

## 6. Component composition

- **FormField wrapping (canonical).** SelectField is typically wrapped in a
  FormField. `name` alignment is the same as TextField.
- **Standalone usage.** Permitted; the trigger renders without an external
  label.
- **Form-element parenting.** Radix attaches a hidden native `<select>` for
  form submission, so `name` participates in standard form posting.

---

## 7. Deferred features

Out of scope for the M1 baseline:

- **Multi-select** — separate component.
- **Async / virtualised options** — large option lists.
- **Per-option `disabled`** — Radix supports it, contract does not yet
  expose it.
- **Option grouping** — `<optgroup>` equivalent (Radix supports it; not yet
  in props).
- **Option icons / two-line items / descriptions** — slot-style custom
  rendering.
- **Searchable / typeahead** — combobox behaviour (a separate component).
- **`onOpenChange` event** — observe popover open/close transitions.
- **`size` variants** — currently only one vertical size (matches TextField
  `'md'`); future `'sm'` and `'lg'` to align with TextField sizing.
- **HTML attribute passthrough.**

---

## 8. Open questions (for council)

1. **Searchable variant.** A typeahead-searchable variant is high-demand
   for option lists with > ~20 entries. Should it be a SelectField prop
   (`searchable?: boolean`) or a separate ComboBox component?
2. **Per-option `disabled`.** Cheap to add. M1 or fast-follow?
3. **`size` variants.** TextField has `'sm' | 'md' | 'lg'`. SelectField
   doesn't expose `size` today. Should the contract include it (matching
   TextField) and the implementation follow, or document the gap?

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
| G-SF1 | Critical | `onOpenChange` / popover lifecycle events undocumented | [RESOLVED 2026-06-05] §7 strengthened: popover opens/closes silently; no lifecycle callback; async-load pattern named |
| G-SF2 | Critical | No async-load (`OnRead`) contract | [RESOLVED 2026-06-05] §7: async options deferred; canonical workaround is host-managed state |
| G-SF3 | High | `value=""` as no-selection sentinel not justified | [RESOLVED 2026-06-05] §3.1 now states: empty string is the null-equivalent; numeric IDs must serialize at boundary |
| G-SF4 | High | Filtering / typeahead behavior absent | [RESOLVED 2026-06-05] §7: typeahead deferred; Radix passive key-match noted |
| G-SF5 | Medium | Per-option disabled deferred as silent-fail | [ACCEPTED-RISK 2026-06-05] §7 note added: per-option `disabled` field is silently ignored in M1 |
| G-SF6 | Medium | `aria-invalid` resting state | [ACCEPTED-RISK 2026-06-05] Same resolution as G-TF3 (attribute omitted when false) |
| G-SF7 | Medium | `loading?: boolean` prop and `aria-busy` wiring absent — no visual loading state for async-option-fetch pattern; trigger shows no indicator while options are in-flight | Fix-deferred M2 — add `loading?: boolean`; render a spinner in the trigger suffix slot; set `aria-busy="true"` on the Radix trigger; tracked as RA-8 |
| G-SF8 | Medium | When `loading=true`, the popover should not open (options not yet available); current behavior is undefined — Radix opens on click regardless | Fix-deferred M2 — when `loading=true`, disable the Radix trigger (`disabled` or `pointer-events-none`) so the popover stays closed until options arrive; tracked as RA-8 |

---

## Kendo-parity expansion (2026-06-11 — spec-first; see _shared/design/polish/kendo-spec-audit/dropdowns.md)

**Status:** Draft — promote to Accepted on council review.
**Wave:** wave-E1 (dropdowns validation + event surface pass).
**Audit baseline:** SelectField ~47% Kendo-minimum coverage. P1 misses: `required`, `onOpenChange`
popup lifecycle events, `loading` state (G-SF7/G-SF8).

---

### §E1-1 FR-1 adoption — validation contract (family ruling FR-1)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-1.

Add to `SelectFieldProps`:

```typescript
required?: boolean   // default: false
```

`error` is already present. `required` drives:
- `aria-required="true"` on the Radix `<Select.Trigger>`.
- `FormFieldContext.required = true` so the FormField asterisk renders.

`validationMessage` is NOT added — FR-1 §3.

**Closes:** audit P1 `required` miss.

**wave-E1**

---

### §E1-2 FR-2 adoption — popup lifecycle events (family ruling FR-2; closes G-SF1 extension)

See `_shared/design/polish/family-rulings-2026-06-11.md` FR-2.

Add to `SelectFieldProps`:

```typescript
onFocus?: React.FocusEventHandler
onBlur?: React.FocusEventHandler
open?: boolean
onOpenChange?: (open: boolean) => void
```

`open` / `onOpenChange` map directly to Radix `<Select.Root open={open} onOpenChange={onOpenChange}>`.
Both controlled (`open` provided) and uncontrolled (omitted) modes are supported.

`onOpenChange` enables the canonical lazy-load pattern:

```tsx
const [isOpen, setIsOpen] = useState(false)
const { data: options } = useQuery(['options'], fetchOptions, { enabled: isOpen })

<SelectField
  name="category"
  value={value}
  onValueChange={setValue}
  options={options ?? []}
  open={isOpen}
  onOpenChange={setIsOpen}
  loading={isOpen && !options}
/>
```

**Closes:** G-SF1 extension (audit P1 `onOpenChange`).

**wave-E1**

---

### §E1-3 FR-3 size variants (family ruling FR-3; closes G-SF4 analog / audit P2 → wave-E1)

Add to `SelectFieldProps`:

```typescript
size?: 'sm' | 'md' | 'lg'   // default: 'md'
```

`size` controls trigger height and font size (same vocabulary as TextField). The Radix
`<Select.Trigger>` receives the appropriate size token classes. Existing single-size behavior
becomes `'md'`.

**wave-E1**

---

### §E1-4 Loading state with popup suppression (closes G-SF7/G-SF8; audit P1)

Add to `SelectFieldProps`:

```typescript
loading?: boolean   // default: false
```

**Semantics:**

1. Animated spinner in trigger suffix slot (replaces Radix `<Select.Icon>`).
2. `aria-busy="true"` on the Radix trigger.
3. **Popup suppression:** when `loading={true}`, the Radix `<Select.Root>` trigger is set to
   `disabled` or the click is intercepted to prevent opening. Radix `<Select.Root open={false}>`
   is the cleanest mechanism when `loading={true}`.
4. Keyboard shortcut (Space / ArrowDown to open) is also suppressed while loading.

**Closes:** G-SF7 (loading indicator), G-SF8 (popup suppression while loading).

**wave-E1**

---

### §E1-5 Adaptive mode note

Radix `@radix-ui/react-select` does not provide a built-in adaptive / responsive popup mode.
SelectField will not adopt this Kendo capability in wave-E1. Note added to §7 Deferred.

**wave-E2+ or WONT-FIX (Radix limitation)**

---

### §E1-6 P2 deferred (wave-E2+)

| Feature | Wave |
|---|---|
| Per-option `disabled` (G-SF5) | wave-E2+ (Radix supports; surface the prop) |
| Option grouping (`{ groupLabel, options }[]`) | wave-E2+ |
| `itemRender` / custom option content | wave-E2+ |
| `clearButton` / clearable | wave-E2+ |
| Virtual scrolling | wave-E2+ |
| Adaptive mode | wave-E2+ (Radix limitation) |
