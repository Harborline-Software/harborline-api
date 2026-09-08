# ErrorCard — Semantic Contract

- **Component:** ErrorCard
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./ErrorCard.Interaction.md) · [Accessibility](./ErrorCard.Accessibility.md) · [Styling](./ErrorCard.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/ErrorCard.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled error display card

---

## 1. Purpose

ErrorCard is a **self-contained error state block** with an optional retry
action. It renders a bordered red card suitable for page-level errors,
panel-level errors, or compact inline error notices. Three sizing variants
control padding density.

ErrorCard vs Alert:

| Criterion | ErrorCard | Alert (error variant) |
| --- | --- | --- |
| Always error | Yes — only one semantic | No — 4 variants |
| Retry action | Built-in optional Retry button | `action` slot (any ReactNode) |
| Sizing variants | `page` / `default` / `compact` | Single size |
| Title element | `<h2>` (page) or `<p>` | Title string |

---

## 2. Data model

```typescript
interface ErrorCardProps {
  title: string
  message?: string
  onRetry?: () => void
  variant?: 'page' | 'default' | 'compact'
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `title` | `string` | _required_ | Primary error message. Rendered as `<h2>` in `page` variant, `<p>` otherwise. |
| `message` | `string` | — | Optional supporting detail below the title. |
| `onRetry` | `() => void` | — | When provided, renders a "Retry" button. |
| `variant` | `'page' \| 'default' \| 'compact'` | `'default'` | Controls padding density and title element. |

### 3.1 Variant semantics

| `variant` | Semantic | Padding | Title element |
| --- | --- | --- | --- |
| `page` | Full-page error (route load failure) | `p-8` | `<h2>` bold, large |
| `default` | Panel / section error | `p-6` | `<p>` semibold |
| `compact` | Inline / cell error | `p-4` | `<p>` semibold |

---

## 4. Events

| Event | Signature | Trigger |
| --- | --- | --- |
| `onRetry` | `() => void` | Retry button clicked. |

---

## 5. Composition

### Page-level API failure

```tsx
<ErrorCard
  variant="page"
  title="Failed to load properties"
  message="We could not retrieve your property list. Please check your connection."
  onRetry={() => refetch()}
/>
```

### Inline error in a panel

```tsx
<ErrorCard title="Could not load lease details" onRetry={retryLease} />
```

---

## 6. Deferred features

- **`children` slot** — no rich body content; `message` is string-only.
- **Icon override** — no icon is rendered; an optional error icon is not
  supported.
- **Severity levels** — always rendered as an error (red); no warning/info
  variants.
