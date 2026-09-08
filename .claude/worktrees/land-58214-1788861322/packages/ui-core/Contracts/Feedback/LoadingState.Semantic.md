# LoadingState — Semantic Contract

- **Component:** LoadingState
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./LoadingState.Interaction.md) · [Accessibility](./LoadingState.Accessibility.md) · [Styling](./LoadingState.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/LoadingState.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled loading state placeholder

---

## 1. Purpose

LoadingState is a **minimal loading placeholder** that communicates to the
user that content is being fetched. It provides two layout variants: a
`page` variant that fills a constrained area with centered text, and an
`inline` variant for compact in-flow loading labels.

LoadingState vs Spinner/Loader (from the Feedback family):

| Criterion | LoadingState | Spinner / Loader |
| --- | --- | --- |
| Visual treatment | Text-only label | Animated spinner graphic |
| Variants | `page` (constrained area) / `inline` (text) | Icon-centric |
| Typical context | Panel loading state, inline status | Button loading, page skeleton |

---

## 2. Data model

```typescript
interface LoadingStateProps {
  label: string
  variant?: 'page' | 'inline'
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
| --- | --- | --- | --- |
| `label` | `string` | _required_ | The loading message displayed to the user (e.g., "Loading properties…"). |
| `variant` | `'page' \| 'inline'` | `'page'` | Controls layout. `page` = constrained centered area; `inline` = plain text span. |

### 3.1 Variant semantics

| `variant` | Rendered element | Layout |
| --- | --- | --- |
| `page` | `<div>` | `flex items-center justify-center h-48` — centered in a 192px tall area |
| `inline` | `<p>` | Plain inline paragraph |

---

## 4. Events

Display-only — no events.

---

## 5. Composition

### Page-level panel loading

```tsx
{loading ? <LoadingState label="Loading properties…" /> : <PropertiesTable />}
```

### Inline loading in a header

```tsx
<LoadingState label="Fetching updates…" variant="inline" />
```

---

## 6. Deferred features

- **Spinner animation** — currently text-only; no animated icon.
- **`children` slot** — no slot for custom loading content.
- **Progress tracking** — no `progress` prop for determinate loading.
