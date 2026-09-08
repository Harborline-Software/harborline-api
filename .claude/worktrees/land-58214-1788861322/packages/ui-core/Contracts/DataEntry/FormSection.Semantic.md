# FormSection — Semantic Contract

- **Component:** FormSection
- **ADR 0017 family:** DataEntry
- **Contract type:** Semantic
- **Status:** Accepted
- **Companion contracts:** [Interaction](./FormSection.Interaction.md) · [Accessibility](./FormSection.Accessibility.md) · [Styling](./FormSection.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormSection.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)
- **Foundation:** none — hand-rolled form section wrapper with optional legend

---

## 1. Purpose

FormSection groups related form fields under a visible heading and optional
description. It renders a `<fieldset>` with a visually-hidden `<legend>` for
screen-reader grouping, and a visible `<h3>` heading for sighted users. It
supports a column grid layout for its children.

---

## 2. Data model

```typescript
interface FormSectionProps {
  title: string
  description?: string
  children: React.ReactNode
  columns?: 1 | 2 | 3
  className?: string
}
```

---

## 3. Props — semantics and defaults

| Prop | Type | Default | Meaning |
|---|---|---|---|
| `title` | `string` | _required_ | Section heading. Rendered both as a visually-hidden `<legend>` (for AT grouping) and a visible `<h3 aria-hidden="true">`. |
| `description` | `string` | — | Optional supplementary text rendered below the heading. |
| `children` | `ReactNode` | _required_ | The form fields within this section, rendered in a CSS grid. |
| `columns` | `1 \| 2 \| 3` | `1` | Number of columns in the field grid. Responsive: 2-col stacks to 1 at narrow viewports; 3-col stacks to 2 at `sm` and 1 at narrow. |
| `className` | `string` | — | Additional CSS classes on the root `<fieldset>`. |

### 3.1 Column grid mapping

| `columns` | Grid classes |
|---|---|
| `1` | `grid-cols-1` |
| `2` | `grid-cols-1 sm:grid-cols-2` |
| `3` | `grid-cols-1 sm:grid-cols-2 lg:grid-cols-3` |

### 3.2 Title rendering: dual-channel

The `title` string renders in two places:
1. `<legend className="sr-only">{title}</legend>` — AT reads this as the
   fieldset group label.
2. `<h3 aria-hidden="true">{title}</h3>` — visible heading for sighted users;
   hidden from AT to avoid double-reading.

---

## 4. Composition

FormSection wraps its children in a `<fieldset>` which groups all contained
form controls semantically. The `<fieldset>` has `border-0 p-0 m-0` to remove
browser default styling.

Children are placed inside a CSS grid (`grid gap-4 {column-classes}`).

---

## 5. Known gaps

| Gap ID | Severity | Description | Disposition |
|---|---|---|---|
| G-FS1 | Medium | The dual `<legend sr-only>` + `<h3 aria-hidden>` pattern is fragile: if a host wraps FormSection in a component that also renders the title text (e.g. a card header), AT reads the title twice — once from the `<legend>` and once from the host heading. Implementors must audit host context. | Accepted-risk M1 — documented here |
| G-FS2 | Low | `<h3>` is hardcoded as the heading level — correct for a section nested inside a `<main>/<section>/<h2>`, but wrong in a context where the section is a top-level `<h1>` region or deeply nested. A `headingLevel?: 2 \| 3 \| 4` prop is deferred. | Accepted-risk M1; add `headingLevel` prop in M2 if sections appear at multiple heading depths |

---

## 6. Deferred features

- **Collapsible sections.** No expand/collapse in M1.
- **Section-level error summary.**
- **Conditional visibility** of entire sections.
