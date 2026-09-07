# Editor — Accessibility Contract

- **Component:** Editor
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./Editor.Semantic.md) · [Interaction](./Editor.Interaction.md) · [Styling](./Editor.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/Editor.tsx`
- **Catalog row:** #51 Editor (`app-priority: medium`, `library-scope: planned`)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. ARIA structure

| Attribute | Element | Value |
|---|---|---|
| `<div>` | Outer container | No role |
| `<div>` | Toolbar | No role in M1 |
| `<button type="button">` | Each tool button | Keyboard focusable |
| `title={tool}` | Each tool button | Tooltip hint; NOT an accessible name — Gap G-ED4 |
| `disabled` | Tool buttons | When `disabled=true` |
| `<textarea>` | Edit surface | Implicit `role="textbox"` |
| `disabled` | `<textarea>` | When `disabled=true` |
| `placeholder` | `<textarea>` | Placeholder text |

---

## 2. Labeling

No `id` prop on the textarea — host cannot use `<label htmlFor>`. Host must wrap in `<label>` or provide `aria-label` (not exposed as a prop — Gap G-ED5).

---

## 3. Toolbar button names

Tool buttons use `title` (tooltip) not `aria-label`. Some AT environments announce `title` as the accessible name; others do not. For reliable AT labeling, `aria-label` should be used instead of `title`.

---

## 4. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-ED4 | Medium | Toolbar buttons use `title` not `aria-label` — AT name announcement is browser-dependent | Accepted-risk M1; functional on most AT |
| G-ED5 | Medium | No `aria-label` or `id` prop on textarea — label association requires wrapping `<label>` | Accepted-risk M1 |
| G-ED6 | Low | Toolbar has no `role="toolbar"` — AT users don't know it's a toolbar | Accepted-risk M1 |
