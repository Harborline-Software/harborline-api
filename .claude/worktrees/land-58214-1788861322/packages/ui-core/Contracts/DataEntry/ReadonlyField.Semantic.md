# ReadonlyField — Semantic Contract

- **Component:** ReadonlyField
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ReadonlyField.Interaction.md) · [Accessibility](./ReadonlyField.Accessibility.md) · [Styling](./ReadonlyField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ReadonlyField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled readonly display field

---

## 1. Purpose

ReadonlyField displays a labelled key-value pair in a definition-term /
definition-detail (`<dt>` / `<dd>`) structure. It is used in read-only
detail views, summaries, and "confirm before submit" screens where form data
is shown non-interactively. An optional hint text is supported.

---

## 2. Data model

```typescript
interface ReadonlyFieldProps {
  label: string
  value: React.ReactNode
  hint?: string
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `label` | `string` | _required_ | The field label. Rendered as a `<dt>` (definition term). |
| `value` | `ReactNode` | _required_ | The field value. Rendered as a `<dd>` (definition description). When `null` or `undefined`, a muted em-dash `—` is rendered. |
| `hint` | `string` | — | Optional supplementary text rendered below the value. |
| `className` | `string` | — | Additional CSS classes on the root `<div>`. |

### 3.1 Null/undefined value handling

```tsx
{value ?? <span className="text-gray-400 italic">—</span>}
```

A falsy `value` (null / undefined) renders a styled em-dash. An empty string
`""` renders as empty text (not the em-dash sentinel).

---

## 4. Events

ReadonlyField has no event handlers. It is non-interactive.

---

## 5. Composition

ReadonlyField renders a `<div>` containing `<dt>` and `<dd>` elements. It does
NOT render a `<dl>` wrapper — hosts that want proper definition-list semantics
should wrap multiple ReadonlyField instances in a `<dl>`.

---

## 6. Deferred features

- **Copy-to-clipboard affordance.**
- **Editable toggle** (switch to InlineEdit on click).
- **`<dl>` wrapper emission.** Currently the host must supply it.
