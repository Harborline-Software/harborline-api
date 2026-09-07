# TagInput — Interaction Contract

- **Component:** TagInput
- **ADR 0017 family:** DataEntry
- **Contract type:** Interaction
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TagInput.Semantic.md) · [Interaction](./TagInput.Interaction.md) · [Accessibility](./TagInput.Accessibility.md) · [Styling](./TagInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/TagInput.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Scope

This contract covers tag addition, tag removal, blur-commit, keyboard shortcuts,
and the disabled and max-tags states.

---

## 2. Adding a tag — `addTag(raw)`

1. Trims the input string.
2. Guards: if trimmed is empty, OR already in `value`, OR `maxTags` reached →
   no-op.
3. Calls `onChange?.([...value, trimmedTag])`.
4. Resets the text input: `setInput('')`.

---

## 3. Removing a tag — `removeTag(tag)`

1. Calls `onChange?.(value.filter(t => t !== tag))`.

---

## 4. Keyboard handlers on the text input

| Key | Behaviour |
|---|---|
| `Enter` | `e.preventDefault()`, calls `addTag(input)`. |
| `,` (comma) | `e.preventDefault()`, calls `addTag(input)`. Use comma to quickly add multiple tags without reaching for Enter. |
| `Backspace` (empty input) | If `input === ''` and `value.length > 0`, removes the LAST tag (`removeTag(value[value.length - 1])`). |

---

## 5. Blur-commit

On `onBlur` of the text input: if `input.trim()` is non-empty, calls `addTag(input)`. This ensures partial input is committed when focus leaves the field.

---

## 6. Per-tag remove button

Each tag chip has a `<button type="button" onClick={() => removeTag(tag)}>`
with `tabIndex={-1}` (excluded from the Tab order) and `aria-label="Remove
{tag}"`.

---

## 7. Disabled mode

When `disabled === true`:
- The wrapper div receives `pointer-events-none opacity-60 bg-gray-50`.
- The text input also receives `disabled`.
- No interaction is possible.

---

## 8. Max tags

When `value.length >= maxTags`:
- Text input receives `disabled` attribute.
- Placeholder is hidden (empty string placeholder).

---

## 9. Known gaps

| # | Gap | Impact |
|---|---|---|
| G1 | Remove buttons have `tabIndex={-1}` — keyboard-only users cannot remove tags via Tab | Tags cannot be removed by keyboard-only users without Backspace |
| G2 | No arrow-key navigation between tags | Standard tag input UX (arrow ← → between chips) absent |
| G3 | Comma `,` as add trigger may conflict with input values that legitimately contain commas | No escaping mechanism |
