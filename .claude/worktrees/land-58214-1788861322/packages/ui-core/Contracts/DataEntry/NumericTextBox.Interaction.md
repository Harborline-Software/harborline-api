# NumericTextBox — Interaction Contract

- **Component:** NumericTextBox
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./NumericTextBox.Semantic.md) · [Accessibility](./NumericTextBox.Accessibility.md) · [Styling](./NumericTextBox.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/NumericTextBox.tsx`
- **Catalog row:** #90 NumericTextBox (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Focus / blur

- **Focus** → `setEditing(true)`, `setRaw(current != null ? String(current) : '')`
- **Blur** → `commit(e.target.value)`
- **Enter key** → `commit(raw)`

---

## 2. Commit behavior

`commit(str)`:
1. `parseFloat(str)` — NaN → `null`
2. Non-NaN → `clamp(n)`: clamps to [min, max]
3. Updates internal state (uncontrolled) and calls `onChange(next | null)`
4. Sets `editing=false`

---

## 3. Spinner buttons

`spin(dir: 1 | -1)`: `clamp((current ?? 0) + dir * step)` → updates state → calls `onChange(next)`.

Increment button disabled when `current >= max`.
Decrement button disabled when `current <= min`.

Spinner buttons are `tabIndex={-1}` — not reachable via keyboard Tab.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-NTB1 | Medium | `aria-invalid` not set — error state not communicated to AT | Accepted-risk M1 |
| G-NTB2 | Medium | `aria-describedby` not supported — no hint/error linking | Accepted-risk M1 |
| G-NTB3 | Low | Spinner buttons are not keyboard-reachable (`tabIndex={-1}`) — no keyboard spinner support | Accepted-risk M1 |
| G-NTB4 | Low | `format` prop only supports `'c'` and `'p'` prefixes — other format strings fall through to `toFixed()` | Accepted-risk M1 |
