# TimePicker — Accessibility Contract

- **Component:** TimePicker
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TimePicker.Semantic.md) · [Interaction](./TimePicker.Interaction.md) · [Styling](./TimePicker.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/TimePicker.tsx`
- **Catalog row:** #137 TimePicker (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `type="time"` | `<input>` | Implicit `role="textbox"` / time semantics |
| `id={id}` | `<input>` | For `<label htmlFor>` association |
| `name={name}` | `<input>` | For form submission |
| `disabled` | `<input>` | When disabled |
| `min` / `max` | `<input>` | Native time constraints |

---

## 2. Label association

`id` prop is exposed — host associates a visible label:
```tsx
<label htmlFor="appointment-time">Appointment time</label>
<TimePicker id="appointment-time" ... />
```

---

## 3. AT behavior

Native `<input type="time">` provides built-in time picker accessibility. AT announces "time, [current value or empty]" and supports keyboard navigation within the native picker. This behavior is platform-native and consistent.

---

## 4. Known gaps

None.

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-1 (aria-required, aria-invalid), FR-2 (onFocus/onBlur — already native-forwarded).
> Supersedes: nothing — additive.

### 5. ARIA attributes (Wave-N)

| Attribute | Element | Value | Note |
|---|---|---|---|
| `aria-required` | `<input>` | `"true"` when `required` prop set (FR-1) | — |
| `aria-invalid` | `<input>` | `"true"` when `error=true` (FR-1) | — |
| `aria-describedby` | `<input>` | `ariaDescribedBy` prop value | Hint/error linking |
| `aria-labelledby` | `<input>` | `ariaLabelledBy` prop value | When provided; supersedes/augments `htmlFor` label |
| `aria-label` | `<input>` | `ariaLabel` prop value | Fallback |

### 6. nowButton accessibility

The "Now" button (when `nowButton=true`) carries `aria-label="Set to current time"`. It is a `type="button"` adjacent to the input. AT announces it as a separate interactive control — not part of the time input itself.

### 7. Custom popup ARIA (future wave)

When the custom popup panel wave ships: the popup container uses `role="dialog"` + `aria-label="Select time"` + `aria-modal="true"`. Each time column (hours, minutes, seconds) uses `role="listbox"` with `aria-label="{column name}"`. Each time option uses `role="option"` + `aria-selected`. Focus management per WAI-ARIA Listbox pattern.
