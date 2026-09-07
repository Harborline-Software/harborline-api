# FormSection — Styling Contract

- **Component:** FormSection
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FormSection.Semantic.md) · [Interaction](./FormSection.Interaction.md) · [Accessibility](./FormSection.Accessibility.md) · [Styling](./FormSection.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FormSection.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Fieldset reset

```
border-0 p-0 m-0
```

Removes all browser-default `<fieldset>` decoration (border, padding, margin).

---

## 2. Heading area

Wrapper: `mb-4`

Title `<h3>`: `text-sm font-semibold text-gray-900`

Description `<p>`: `mt-0.5 text-sm text-gray-500`

---

## 3. Field grid

```
grid gap-4 {column-classes}
```

Column classes by `columns` prop:

| `columns` | Classes |
|---|---|
| `1` | `grid-cols-1` |
| `2` | `grid-cols-1 sm:grid-cols-2` |
| `3` | `grid-cols-1 sm:grid-cols-2 lg:grid-cols-3` |

---

## 4. Visual state inventory

FormSection has no interactive visual states. It is a layout-only component.

---

## 5. Token notes

FormSection uses raw Tailwind color values (`text-gray-900`, `text-gray-500`)
rather than semantic design-system tokens. A future token pass should map these
to `--sf-section-title-fg` and `--sf-section-description-fg`.
