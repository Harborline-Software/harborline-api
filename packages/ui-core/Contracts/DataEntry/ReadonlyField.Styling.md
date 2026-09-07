# ReadonlyField — Styling Contract

- **Component:** ReadonlyField
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./ReadonlyField.Semantic.md) · [Interaction](./ReadonlyField.Interaction.md) · [Accessibility](./ReadonlyField.Accessibility.md) · [Styling](./ReadonlyField.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/ReadonlyField.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Root layout recipe

```
flex flex-col gap-1
```

---

## 2. Label (`<dt>`)

```
text-sm font-medium text-gray-500
```

---

## 3. Value (`<dd>`)

```
text-sm text-gray-900
```

Empty sentinel span: `text-gray-400 italic`

---

## 4. Hint (`<p>`)

```
text-xs text-gray-400
```

---

## 5. Visual state inventory

ReadonlyField has no interactive visual states. Colors are fixed.

---

## 6. Token notes

ReadonlyField uses raw Tailwind gray values. Future token pass should map to:
- `text-gray-500` → `--sf-readonly-label-fg`
- `text-gray-900` → `--sf-readonly-value-fg`
- `text-gray-400` → `--sf-readonly-hint-fg` (contrast review needed — may need
  upgrade to `text-gray-500`)
