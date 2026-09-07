# CodeBlock — Semantic Contract

- **Component:** CodeBlock
- **ADR 0017 family:** Feedback
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./CodeBlock.Interaction.md) · [Accessibility](./CodeBlock.Accessibility.md) · [Styling](./CodeBlock.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/feedback/CodeBlock.tsx`
- **Catalog row:** #A35 CodeBlock (`app-priority: low`, `library-scope: planned`) — Harborline-native syntax-highlighted code display block
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — native `<pre>/<code>` with optional syntax highlighting

---

## 1. Component purpose

**CodeBlock** — a styled code display with optional filename header, language label, line numbers, and copy-to-clipboard button.

---

## 2. Props

```typescript
interface CodeBlockProps extends React.HTMLAttributes<HTMLDivElement> {
  code: string
  language?: string
  filename?: string
  showLineNumbers?: boolean  // default: false
  onCopy?: () => void
}
```

---

## 3. Copy behavior

Uses `navigator.clipboard.writeText(code)`. Copied state is reset to false after 2 seconds. `onCopy` fires immediately after clipboard write.

---

## 4. Header display

When `filename` or `language` is provided, a header bar renders above the code. The header shows `filename` (if provided) or `language` (if only language is set). When neither is provided, the copy button renders absolutely positioned on the scroll container.

---

## 5. Line numbers

When `showLineNumbers=true`, each line is rendered as a `table-row` pair: a non-selectable line number `<span>` + the line text `<span>`.
