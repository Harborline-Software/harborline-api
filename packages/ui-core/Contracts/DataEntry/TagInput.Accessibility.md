# TagInput — Accessibility Contract

- **Component:** TagInput
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./TagInput.Semantic.md) · [Interaction](./TagInput.Interaction.md) · [Accessibility](./TagInput.Accessibility.md) · [Styling](./TagInput.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/TagInput.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

TagInput's primary interactive element is a text `<input>`. Tags are rendered
as inline chips with `<button>` remove controls. The input has an `aria-label`
and optionally `aria-describedby` for hint wiring. Per-tag remove buttons have
`aria-label` but `tabIndex={-1}`.

---

## 2. ARIA structural roles

| Element | Role | Notes |
|---|---|---|
| `<label>` | label | `htmlFor={id}` — label for the text input |
| Text `<input type="text">` | `textbox` | `aria-label={label}`, `aria-describedby`, `aria-invalid` |
| Tag chip `<span>` | generic | No ARIA role — readable as text |
| Remove `<button>` | `button` | `aria-label="Remove {tag}"`, `tabIndex={-1}` |
| Remove button SVG | decorative | `aria-hidden="true"` |
| Hint `<p>` | paragraph | Has `id="{id}-hint"` |
| Error `<p>` | alert | `role="alert"` |

---

## 3. Input ARIA

```tsx
aria-label={label}
aria-describedby={hint ? `${id}-hint` : undefined}
aria-invalid={hasError || undefined}
```

- `aria-label` duplicates the visible `<label>` when `label` is provided. This
  is redundant (label association via `htmlFor`/`id` is sufficient) but
  harmless.
- `aria-describedby` wires the hint paragraph when `hint` is present.
- `aria-invalid` signals error state programmatically.

**WCAG citations:**
- WCAG 2.2 SC 1.3.1 Info and Relationships
- WCAG 2.2 SC 3.3.1 Error Identification
- WCAG 2.2 SC 4.1.2 Name, Role, Value

---

## 4. Per-tag remove buttons

```tsx
aria-label={`Remove ${tag}`}
tabIndex={-1}
```

Remove buttons are reachable by AT (announced when reading the page linearly)
but NOT reachable via Tab (excluded from tab order). This means keyboard-only
users who navigate by Tab cannot focus the remove buttons.

The Backspace shortcut (when the text input is empty) provides an alternative
path to remove tags — but it only removes the last tag, not a specific one.

**WCAG citations:**
- WCAG 2.2 SC 2.1.1 Keyboard (partial gap — Backspace alternative exists)
- WCAG 2.2 SC 4.1.2 Name, Role, Value

---

## 5. Error announcement

`<p role="alert">` announces the error message assertively when it appears.

**WCAG citations:**
- WCAG 2.2 SC 3.3.1 Error Identification
- WCAG 2.2 SC 4.1.3 Status Messages

---

## 6. Focus visible — field container

```
focus-within:ring-2 focus-within:ring-blue-500 focus-within:border-blue-500
```

The outer field container uses `focus-within` so the ring appears when ANY
child (the text input) is focused. This is a valid AT-friendly pattern for
a composite input area.

---

## 7. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | Remove buttons have `tabIndex={-1}` — not Tab-reachable | High | Make remove buttons Tab-reachable (remove `tabIndex={-1}`), add explicit focus ring |
| G2 | No live region announces tag additions/removals to AT | Medium | Add `aria-live="polite"` region reporting e.g. "Tag 'foo' added" / "Tag 'foo' removed" |
| G3 | `aria-label={label}` duplicates `htmlFor`/`id` association | Low | Remove `aria-label` from input when `label` is provided |
