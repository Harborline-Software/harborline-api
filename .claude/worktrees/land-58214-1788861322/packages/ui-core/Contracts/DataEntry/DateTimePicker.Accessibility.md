# DateTimePicker — Accessibility Contract

- **Component:** DateTimePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DateTimePicker.Semantic.md) · [Interaction](./DateTimePicker.Interaction.md) · [Styling](./DateTimePicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateTimePicker.tsx`
- **Catalog row:** #40 DateTimePicker (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `type="datetime-local"` | `<input>` | Implicit date+time semantics |
| `disabled` | `<input>` | When disabled |

---

## 2. Label association

No `id` prop exposed in M1. Host must wrap in a `<label>` for label association:
```tsx
<label>
  Meeting time
  <DateTimePicker ... />
</label>
```

---

## 3. AT behavior

Native `<input type="datetime-local">` provides platform-native accessibility for date+time selection. AT handling is browser/OS-dependent.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DTP3 | Medium | No `id` prop exposed — cannot use `<label htmlFor>` pattern | Accepted-risk M1; host must use wrapping label |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (aria-required, aria-invalid), FR-2 (focus events).
> Supersedes: G-DTP3 (no id — RESOLVED at Wave-N).

### 5. ARIA attributes (Wave-N)

| Attribute | Element | Value | Note |
|---|---|---|---|
| `id` | `<input>` | `id` prop value | Resolves G-DTP3 — enables `<label htmlFor>` |
| `name` | `<input>` | `name` prop value | Form participation |
| `aria-required` | `<input>` | `"true"` when `required` prop set (FR-1) | — |
| `aria-invalid` | `<input>` | `"true"` when `error=true` (FR-1) | — |
| `aria-describedby` | `<input>` | `ariaDescribedBy` prop value | Hint/error linking |
| `aria-labelledby` | `<input>` | `ariaLabelledBy` prop value | When provided |
| `aria-label` | `<input>` | `ariaLabel` prop value | Fallback |

### 6. Label association (Wave-N)

With `id` prop now exposed, the preferred pattern is `<label htmlFor={id}>` rather than the M1 wrapping-label workaround. Both patterns remain valid at Wave-N.

### 7. AT behavior note

Native `<input type="datetime-local">` accessibility varies across browsers. The AT-behavior note in §3 remains accurate. The Wave-N ARIA attributes (aria-required, aria-invalid, aria-describedby) supplement native semantics without replacing them.
