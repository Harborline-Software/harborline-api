# CodeBlock — Styling Contract

- **Component:** CodeBlock
- **ADR 0017 family:** Feedback
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./CodeBlock.Semantic.md) · [Interaction](./CodeBlock.Interaction.md) · [Accessibility](./CodeBlock.Accessibility.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/CodeBlock.tsx`
- **Catalog row:** (not-in-catalog) CodeBlock (`app-priority: low`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Outer container

`overflow-hidden rounded-lg border border-gray-200 bg-gray-950` + `className` passthrough.

> **M1 note:** Dark-theme colors are hardcoded palette classes (`gray-950`, `gray-900`, `gray-200`).

---

## 2. Header bar (when filename or language)

`flex items-center justify-between border-b border-gray-800 bg-gray-900 px-4 py-2`

Filename: `text-xs font-medium text-gray-300`

Language (no filename): `text-xs text-gray-500`

---

## 3. Copy button

`flex items-center gap-1.5 rounded px-2 py-1 text-xs text-gray-400 transition-colors hover:bg-gray-800 hover:text-gray-200`

Copied state icon: `text-green-400`; text: `text-green-400`

Icons: `h-3.5 w-3.5`

---

## 4. Headerless copy button

`absolute right-2 top-2` on the `div.relative.overflow-x-auto` scroll wrapper.

---

## 5. Code content

Scroll wrapper: `relative overflow-x-auto`

`<pre>`: `p-4 text-sm leading-relaxed text-gray-100`

---

## 6. Line numbers (showLineNumbers)

Line number `<span>`: `table-cell select-none pr-4 text-right text-gray-600` with `minWidth: {digits+1}ch`

Line text `<span>`: `table-cell`

Line row `<span>`: `table-row`
