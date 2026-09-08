# MaskedText — Semantic Contract

- **Component:** MaskedText
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./MaskedText.Interaction.md) · [Accessibility](./MaskedText.Accessibility.md) · [Styling](./MaskedText.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/MaskedText.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled text masking display

---

## 1. Purpose

MaskedText **obscures sensitive string values** (account numbers, API keys,
SSNs, routing numbers) by default, revealing only a configurable trailing
portion. The user can toggle the full value visible via a show/hide button.

---

## 2. Data model

```typescript
interface MaskedTextProps {
  value: string
  visibleChars?: number
  maskChar?: string
  label?: string
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `value` | `string` | _required_ | The sensitive value to display. Displayed fully when revealed; partially masked otherwise. |
| `visibleChars` | `number` | `4` | Number of trailing characters to show in masked state (e.g., last 4 digits of an account number). |
| `maskChar` | `string` | `'•'` | The character used to mask the hidden portion. Repeated `value.length - visibleChars` times. |
| `label` | `string` | — | Accessible label for the value span. When absent, falls back to `value` (revealed) or `'hidden value'` (masked). |
| `className` | `string` | — | Additional classes on the outer `<span>`. |

### 3.1 Masked display

Masked display = `maskChar.repeat(value.length - visibleChars) + value.slice(-visibleChars)`.

Example: `value="1234567890"`, `visibleChars=4` → `••••••7890`.

### 3.2 Reveal toggle

MaskedText manages a `revealed` boolean state internally. When `revealed=true`,
the full `value` is shown. When `revealed=false`, the masked display is shown.
The toggle button switches between the two states.

---

## 4. Events

No externally surfaced events. Toggle is internal state only.

---

## 5. Composition

### Account number in a details panel

```tsx
<dl>
  <dt>Routing number</dt>
  <dd><MaskedText value="021000021" visibleChars={3} label="Routing number" /></dd>
  <dt>Account number</dt>
  <dd><MaskedText value="987654321" label="Account number" /></dd>
</dl>
```

### API key display

```tsx
<MaskedText value="sk-1234567890abcdef" visibleChars={6} maskChar="*" />
```

---

## 6. Deferred features

- **Copy button** — no built-in "Copy to clipboard" action alongside the
  value.
- **`onReveal` / `onHide` callbacks** — reveal state changes are not
  surfaced as events.
- **Controlled reveal** — `revealed` is internal state only; no `revealed`
  prop for host-controlled visibility.
