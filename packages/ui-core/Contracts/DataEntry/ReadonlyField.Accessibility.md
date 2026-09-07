# ReadonlyField — Accessibility Contract

- **Component:** ReadonlyField
- **ADR 0017 family:** DataEntry
- **Contract type:** Accessibility
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ReadonlyField.Semantic.md) · [Interaction](./ReadonlyField.Interaction.md) · [Accessibility](./ReadonlyField.Accessibility.md) · [Styling](./ReadonlyField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ReadonlyField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

ReadonlyField uses HTML definition-term (`<dt>`) / definition-description
(`<dd>`) elements. When multiple ReadonlyField instances are placed inside a
`<dl>`, AT announces each pair as a term-and-description group. Without the
`<dl>` wrapper, the semantic grouping is degraded.

---

## 2. ARIA structural roles

| Element | Implicit role | Notes |
|---|---|---|
| `<dt>` | `term` | AT announces label |
| `<dd>` | `definition` | AT announces value |
| `<p>` (hint) | paragraph | Read in document order |

---

## 3. Definition list requirement

`<dt>` and `<dd>` elements derive their semantic meaning from being children
of a `<dl>`. ReadonlyField renders without a `<dl>` wrapper — the host MUST
wrap multiple ReadonlyField instances in a `<dl>` for correct AT association.

A single ReadonlyField outside a `<dl>` may still be readable by AT as text
content, but the term/definition relationship is not announced.

**WCAG citation:** WCAG 2.2 SC 1.3.1 Info and Relationships.

---

## 4. Null value rendering

The em-dash sentinel:
```tsx
<span className="text-gray-400 italic">—</span>
```

AT reads "—" as an em dash. The italic styling is visual only. Consider
adding `aria-label="not set"` to communicate absence more clearly to AT users.

---

## 5. Keyboard navigation

ReadonlyField is non-interactive — no tabstops, no focus management.

---

## 6. Color contrast

| Surface | Token | Minimum ratio |
|---|---|---|
| Label `<dt>` | `text-gray-500` | Must meet 4.5:1 against background |
| Value `<dd>` | `text-gray-900` | Meets 4.5:1 against white (21:1 ratio) |
| Hint `<p>` | `text-gray-400` | May fall short of 4.5:1 — known gap G1 |
| Em-dash sentinel | `text-gray-400 italic` | Same as hint — known gap G1 |

**WCAG citation:** WCAG 2.2 SC 1.4.3 Contrast (Minimum).

---

## 7. Known gaps

| # | Gap | Severity | Fix path |
|---|---|---|---|
| G1 | `text-gray-400` for hint and empty sentinel may fail 4.5:1 contrast on white bg | Medium | Darken to `text-gray-500` minimum |
| G2 | `<dt>` / `<dd>` outside a `<dl>` loses term/definition semantic | Medium | Host responsibility; document requirement clearly |
| G3 | Em-dash sentinel has no `aria-label` to communicate "empty" | Low | Add `aria-label="not set"` or `aria-label="empty"` |
