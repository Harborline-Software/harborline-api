# DateField — Semantic Contract

- **Component:** DateField
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Semantic (props, events, data model, slots)
- **Status:** Accepted
- **Companion contracts:** [Interaction](./DateField.Interaction.md) · [Styling](./DateField.Styling.md) · [Accessibility](./DateField.Accessibility.md)
- **Related contract:** [FormField.Semantic.md](./FormField.Semantic.md) — typical wrapper for label + hint + error.
- **Reference implementation:** `packages/ui-react/src/components/forms/DateField.tsx`
- **Catalog row:** #37 DateInput (`app-priority: high`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — FormField wrapper around DateInput (hand-rolled date string input)

---

## 1. Purpose

DateField is the M1 date-input control of `@harborline-software/ui-react`. It is a thin
controlled wrapper around a native `<input type="date">` — leveraging the
browser-provided date picker UI rather than a custom calendar widget. This is
a deliberate **M1 baseline choice**: native pickers give first-class keyboard
+ AT support out of the box, free localization on most browsers, and minimal
bundle weight. A richer custom calendar component is a future wave (catalog
has a DatePicker entry).

---

## 2. Data model

DateField is fully controlled. The `value` is an ISO-8601 date string
(`YYYY-MM-DD`), matching the native `<input type="date">` value format.

```typescript
interface DateFieldProps {
  name: string
  value: string        // ISO-8601 date: "YYYY-MM-DD", or "" for empty
  onChange: (v: string) => void
  min?: string         // ISO-8601 lower bound (inclusive)
  max?: string         // ISO-8601 upper bound (inclusive)
  disabled?: boolean
  error?: boolean
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `name` | `string` | _required_ | Field name. Drives `id` and `name` on the native input. |
| `value` | `string` | _required_ | Controlled ISO-8601 date string (`YYYY-MM-DD`), or empty string for "no date selected". |
| `onChange` | `(v: string) => void` | _required_ | Fires when the user picks a new date (via the native picker) or clears the field. Payload is the new ISO-8601 string, or `""` when cleared. |
| `min` | `string` | — | Optional inclusive lower bound; native picker disables earlier dates. Must be ISO-8601. |
| `max` | `string` | — | Optional inclusive upper bound; native picker disables later dates. Must be ISO-8601. |
| `disabled` | `boolean` | `false` | Disables the input. |
| `error` | `boolean` | `false` | Renders the error treatment + sets `aria-invalid={true}`. |

### 3.1 Why ISO-8601 strings (not `Date` objects)

The value is a string, not a JS `Date`, because:

- It matches the native `<input type="date">` API exactly — no parse / format
  round-trip.
- Date objects carry time + timezone information that an all-day date does
  not need (and timezone confusion is a classic bug source).
- Serialising / deserialising to/from JSON is trivial.

Hosts that need `Date` objects must convert at the boundary (e.g.
`new Date(value)` or `Temporal.PlainDate.from(value)`).

### 3.2 Locale + presentation

The displayed date format in the field text is **browser-locale-controlled**:
Chrome on en-US shows `mm/dd/yyyy`, Chrome on en-GB shows `dd/mm/yyyy`. The
underlying `value` is always ISO-8601 regardless. PAO Accessibility should
confirm this is acceptable for AT users in all target locales.

### 3.3 Picker UI

The picker UI is the browser-native picker. Appearance, calendar layout,
keyboard navigation inside the picker, and AT announcements within the picker
are owned by the browser. DateField cannot customise the picker.

### 3.4 HTML attribute passthrough

Not implemented. Known gap.

### 3.5 FormField integration

Same pattern as TextField — reads `describedBy` from `useFormField()` and
threads it onto `aria-describedby` on the native input.

---

## 4. Events — semantics

| Event | Payload | Fired when |
| --- | --- | --- |
| `onChange` | `(v: string)` | The user picks a date in the picker or clears the field (some browsers expose a clear button). Payload is the new ISO-8601 string, or `""` when cleared. |

### 4.1 onChange semantics — commit, not typing

**`onChange` fires on commit, not on intermediate typing.** Native
`<input type="date">` fires its `change` event only when the user completes a
valid date (by picking from the calendar, pressing Tab/Enter after typing all
fields, or when focus leaves the input). Unlike `<input type="text">`, it does
NOT fire on each partial keystroke.

**Cross-browser variance:** Chrome generally withholds `change` until the date
is complete; some browsers may fire with `value=""` for invalid partial input
(e.g. `"2026-06-"`). Hosts should treat `onChange("")` as "no valid date
selected" regardless of cause.

**Invalid typed input:** If the user types an invalid date, the native input
either retains the previous `value` or clears to `""`. The component fires
`onChange("")` in the clear case. Hosts using validation should treat an empty
`onChange` payload as clearing the field.

---

## 5. Slots

No slot extensibility in M1. The picker UI is browser-native (no slot
customisation possible) and the field itself has no adornment slots.

---

## 6. Component composition

- **FormField wrapping (canonical).** Same as TextField.
- **Standalone usage.** Permitted.
- **Form-element parenting.** Native `<input>` participates in form
  submission via `name`.

---

## 7. Deferred features

Out of scope for the M1 baseline:

- **Custom calendar UI** — replace the native picker with a styled calendar
  widget. Future wave / separate DatePicker component.
- **Time-of-day input** — `<input type="datetime-local">` is not supported
  in M1; a DateTimeField may follow.
- **Date range** — a "from / to" pair is not in M1.
- **Locale override** — currently locked to browser locale.
- **Today / clear shortcut buttons** — out of scope.
- **`size` variants** — DateField does not expose `size` in M1.
- **HTML attribute passthrough.**

---

## 8. Open questions (for council)

1. **Native vs custom picker.** Native is a deliberate M1 choice. PAO
   Accessibility should confirm the native picker meets WCAG 2.2 AA in all
   target browsers (Chromium, Firefox, Safari). If not, the contract may
   need to switch to a custom calendar earlier than planned.
2. **`Temporal.PlainDate` adoption.** When `Temporal` reaches Baseline,
   should DateField switch to a `PlainDate` value or keep ISO strings?
   (Leaning: keep strings — they're universal and serialise free.)
3. **Locale override.** Should DateField accept a `locale` prop that, when
   set, overrides browser locale for display? Native input doesn't make
   this easy — only a custom widget can deliver it cleanly.

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
| G-DF1 | High | `onChange` semantics during invalid intermediate states | [RESOLVED 2026-06-05] §4.1 added: native `<input type="date">` fires change only on commit; cross-browser variance noted |
| G-DF2 | High | Invalid-typed-input recovery undocumented | [RESOLVED 2026-06-05] §4.1 added: browser-dependent; `onChange` may fire `""` or prior value |
| G-DF3 | Medium | `min`/`max` enforcement is picker-only | [ACCEPTED-RISK 2026-06-05] §3.1.1 note: typed input bypasses range; native rangeUnderflow only on submit |
| G-DF4 | Medium | Clear-button availability inconsistent across browsers | [ACCEPTED-RISK 2026-06-05] §7 note: Chrome shows clear; Safari does not; host must provide own clear button for guaranteed UX |
| G-DF5 | Medium | Today button / quick-shortcut absent | [ACCEPTED-RISK 2026-06-05] §7 deferred |
