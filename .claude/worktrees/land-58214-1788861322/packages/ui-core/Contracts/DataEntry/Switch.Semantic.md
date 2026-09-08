# Switch — Semantic Contract

- **Component:** Switch
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./Switch.Interaction.md) · [Accessibility](./Switch.Accessibility.md) · [Styling](./Switch.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Switch.tsx`
- **Catalog row:** #130 Switch (`app-priority: medium`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M2 (B1 council migration 2026-06-12; M1 superseded)
- **Foundation:** none — native `<button>` styled as toggle switch (no Radix primitive)

---

## 1. Component purpose

**Switch** — a standalone toggle switch with on/off labels. Distinct from SwitchField (which wraps Switch inside FormField with label/hint/error). Uses a single `<button role="switch">` element (M2 canonical form).

---

## 2. Props

```typescript
interface SwitchProps {
  checked?: boolean
  defaultChecked?: boolean
  onCheckedChange?: (checked: boolean) => void  // CANONICAL name (B1 ruling 2026-06-12)
  /** @deprecated Use onCheckedChange. Retained one wave as alias. */
  onChange?: (checked: boolean) => void
  disabled?: boolean       // default: false
  label?: string           // static label; overrides onLabel/offLabel when set
  onLabel?: string         // default: 'On' — shown when checked and no label
  offLabel?: string        // default: 'Off' — shown when unchecked and no label
  size?: 'sm' | 'md' | 'lg' | 'small' | 'medium' | 'large'  // default: 'md'; FR-3 canonical aliases
  id?: string
  name?: string
  className?: string
  error?: boolean    // FR-1: aria-invalid on button
  required?: boolean // FR-1: aria-required on button
}
```

**Handler name ruling (B1 council 2026-06-12):** `onCheckedChange` is the canonical prop name.
`onChange` is a deprecated alias retained for one migration wave; callers must migrate to `onCheckedChange`.

---

## 3. Label behavior

- `label` provided → always shows `label` text (no state change in text)
- No `label` → shows `onLabel` when `isOn`, `offLabel` when `!isOn`

---

## 4. Controlled / uncontrolled

Controlled when `checked` is provided. Uncontrolled uses internal `internal` state seeded by `defaultChecked ?? false`.

---

## 5. Implementation approach

### M1 (SUPERSEDED — 2026-06-12 B1 migration)

~~Two elements work together: hidden `<input type="checkbox" className="sr-only">` + visual `<span role="switch">`.~~
M1 is NOT the canonical spec. It carried G-SW1 (double-announcement), G-SWI1 (keyboard gap), G-SW3 (aria-invalid gap). All three gaps are closed by M2.

### M2 (CANONICAL — shipping 2026-06-12)

**Single `<button role="switch">` element:**

```tsx
<div className={cn('inline-flex items-center gap-2', disabled && 'cursor-not-allowed opacity-50', className)}>
  <button
    type="button"
    role="switch"
    id={id}
    aria-checked={isOn}
    aria-invalid={error ? 'true' : undefined}
    aria-required={required ? 'true' : undefined}
    disabled={disabled}
    onClick={toggle}
    onKeyDown={/* Space + Enter → toggle */}
  >
    {/* thumb visual */}
  </button>
  {/* Form participation: always submits 'on'|'off' when name is set */}
  {name && <input type="hidden" name={name} value={isOn ? 'on' : 'off'} />}
  {/* label text */}
</div>
```

**Form-submit value semantics (B1 condition 1):** The hidden input ALWAYS submits a value when `name` is set: `'on'` when checked, `'off'` when unchecked. This differs from the M1 checkbox-style (which omitted the field when unchecked). Callers reading submitted form values must expect both states.

**Benefits over M1:** no double-announcement (G-SW1 CLOSED), native keyboard activation via Space + Enter (G-SWI1 CLOSED), `aria-invalid` settable directly on the switch element (G-SW3 CLOSED), single ARIA owner.

---

## FormField context integration (Cohort-2, 2026-06-12)

Switch reads `FormFieldContext` via `useFormField()`. When wrapped in a `<FormField>`, the following props are automatically applied:

| Context value | Activates when | Prop precedence |
|---|---|---|
| `describedBy` | FormField has `hint` or `error` | No prop override — context-only |
| `required` | FormField has `required={true}` | Prop `required` wins if explicitly set |
| `disabled` | FormField has `disabled={true}` | Prop `disabled` wins if explicitly set |

**Standalone (no FormField):** `useFormField()` returns an empty object; all three values are `undefined`; no change to existing standalone behaviour.

**Inside FormField:**
```tsx
<FormField name="notifications" label="Enable notifications" required>
  <Switch name="notifications" />
</FormField>
```
Switch automatically gains `aria-required="true"` and `aria-describedby` wired to the FormField hint/error elements — no explicit prop threading needed.

**Prop-overrides-context rule:** `resolved = prop ?? contextValue ?? false`. A component prop of `disabled={true}` beats a context `disabled={false}`, and vice versa. Passing no prop defers to the context.

**Replaces:** SwitchField (deprecated shim). See [SwitchField.Semantic.md](./SwitchField.Semantic.md) for migration guidance.
