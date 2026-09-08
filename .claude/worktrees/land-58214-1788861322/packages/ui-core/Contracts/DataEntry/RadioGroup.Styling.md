# RadioGroup — Styling Contract

- **Component:** RadioGroup
- **ADR 0017 family:** DataEntry (Forms)
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./RadioGroup.Semantic.md) · [Interaction](./RadioGroup.Interaction.md) · [Accessibility](./RadioGroup.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/RadioGroup.tsx`
- **Catalog row:** #105 RadioGroup (`app-priority: high`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Container

`flex` + orientation class.

| `orientation` | Classes |
|---|---|
| `vertical` (default) | `flex-col gap-2` |
| `horizontal` | `flex-row flex-wrap gap-4` |

---

## 2. Option label wrapper

`flex cursor-pointer items-start gap-2.5`

Disabled: `cursor-not-allowed opacity-60`

---

## 3. Radio input

`mt-0.5 h-4 w-4 shrink-0 cursor-pointer border-gray-300 text-blue-600 focus:ring-2 focus:ring-blue-500 focus:ring-offset-1`

Error state: `border-red-400 text-red-500`

Disabled: `cursor-not-allowed`

> **M1 note:** Colors are hardcoded palette classes, not design tokens.

---

## 4. Option label text

`text-sm text-gray-700` + `opacity-60` when disabled.

---

## 5. Option description

`mt-0.5 text-xs text-gray-500`
