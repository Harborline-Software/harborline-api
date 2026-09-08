# DateInput — Interaction Contract

- **Component:** DateInput
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./DateInput.Semantic.md) · [Accessibility](./DateInput.Accessibility.md) · [Styling](./DateInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/DateInput.tsx`
- **Catalog row:** #37 DateInput (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Change behavior

Native `onChange` → parse `e.target.value` as `new Date(value + 'T00:00:00')` (local midnight, not UTC). Empty string → `null`. Calls `onValueChange(date | null)`.

---

## 2. Min / max constraints

`min` and `max` are passed as YYYY-MM-DD strings to the native input via `toInputValue()`. Browser enforces the constraint in its picker UI. No JavaScript enforcement on direct keyboard entry.

---

## 3. Readonly

`readOnly={readonly}` on the native input. Prevents user input but still focusable and selectable.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-DI1 | Medium | `format` prop is declared but not implemented — display format cannot be customized | Accepted-risk M1 |
| G-DI2 | Medium | `aria-invalid` not set — invalid state is not communicated to AT | Accepted-risk M1 |
| G-DI3 | Low | No `aria-describedby` — callers cannot link error/hint text | Accepted-risk M1 |
| G-DI4 | Low | No JavaScript enforcement of min/max on keyboard entry — browser constraint only | Accepted-risk M1 |

---

## Wave-N expansion (2026-06-11 — kendo-spec-audit coverage)

> Ruling cross-refs: FR-2 (onFocus/onBlur passthrough).
> Supersedes: G-DI1 is partially resolved — `format` remains reserved for the segmented-editor wave; the contract now carries spinners/steps/formatPlaceholder as reserved surface (Semantic Wave-N §10).

### 5. onFocus / onBlur (FR-2)

`onFocus` and `onBlur` forward the native input focus event to the host. Required for FormField focus-ring wiring and for picker hosts that must detect when focus leaves the combined input+calendar composite.

### 6. Spinner interaction (contract-reserved)

When `spinners=true` is active (segmented-editor wave): Up arrow button on segment increases value by `steps[segment] ?? 1`. Down arrow button decreases by the same step. Keyboard `ArrowUp`/`ArrowDown` on a focused segment produce the same effect. Focus moves to the next segment after `steps` wraps a segment value.

For the M1 native-input path, spinner buttons are not rendered regardless of the `spinners` prop value.
