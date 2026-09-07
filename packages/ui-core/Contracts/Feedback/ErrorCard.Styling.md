# ErrorCard — Styling Contract

- **Component:** ErrorCard
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ErrorCard.Semantic.md) · [Interaction](./ErrorCard.Interaction.md) · [Accessibility](./ErrorCard.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/ErrorCard.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

Base: `rounded-lg border border-red-200 bg-red-50` + variant padding.

| Variant | Padding |
| --- | --- |
| `page` | `p-8` |
| `default` | `p-6` |
| `compact` | `p-4` |

---

## 2. Title

| Variant | Classes |
| --- | --- |
| `page` | `<h2>` — `text-xl font-bold text-red-700` |
| `default` / `compact` | `<p>` — `font-semibold text-red-700` |

---

## 3. Message

`mt-1 text-sm text-gray-600`

---

## 4. Retry button

| Variant | Classes |
| --- | --- |
| `compact` | `mt-2 rounded bg-blue-600 px-3 py-1 text-xs text-white hover:bg-blue-700` |
| `page` / `default` | `mt-3 rounded bg-blue-600 px-4 py-2 text-sm text-white hover:bg-blue-700` |
