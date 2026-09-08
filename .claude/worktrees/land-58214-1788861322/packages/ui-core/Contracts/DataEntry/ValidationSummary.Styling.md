# ValidationSummary — Styling Contract

- **Component:** ValidationSummary
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ValidationSummary.Semantic.md) · [Interaction](./ValidationSummary.Interaction.md) · [Accessibility](./ValidationSummary.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ValidationSummary.tsx`
- **Catalog row:** #146 ValidationSummary (`app-priority: high`, `library-scope: v1`)
- **Phase:** ADR 0017-A1 Phase M1

---

## 1. Container

`rounded-md border p-4` + variant class + `className` passthrough.

> **M1 note:** All variant colors are hardcoded Tailwind palette classes, not design tokens.

---

## 2. Variant classes

| Variant | Container | Title |
|---|---|---|
| `error` (default) | `border-red-200 bg-red-50 text-red-800` | `text-red-900` |
| `warning` | `border-amber-200 bg-amber-50 text-amber-800` | `text-amber-900` |

---

## 3. Title

`mb-2 text-sm font-semibold` + variant title class.

---

## 4. Error list

`list-disc list-inside space-y-1`

Each `<li>`: `text-sm`
