# FieldWrapper — Styling Contract

- **Component:** FieldWrapper
- **ADR 0017 family:** DataEntry
- **Contract type:** Styling
- **Status:** Accepted
- **Companion contracts:** [Semantic](./FieldWrapper.Semantic.md) · [Interaction](./FieldWrapper.Interaction.md) · [Accessibility](./FieldWrapper.Accessibility.md) · [Styling](./FieldWrapper.Styling.md)
- **Reference implementation:** `packages/ui-react/src/components/forms/FieldWrapper.tsx`
- **Catalog row:** (not in master catalog — implementation-first component)
- **Phase:** ADR 0017-A1 Phase M1 (wave-N extraction from shipping implementation)

---

## 1. Purpose

FieldWrapper applies a `flex flex-col gap-1.5` stack layout and injects
validity-state border tokens into its child inputs via CSS descendant
selectors. It relies on the host design system's `--sf-color-destructive`
(`border-destructive`) and `--sf-color-success` (`border-success`) CSS custom
properties.

---

## 2. Root layout recipe

```
flex flex-col gap-1.5
```

Applied to the root `<div>`. `className` prop merges additional classes via
`cn()`.

---

## 3. Child slot validity overlay

Applied to the wrapper `<div>` around `{children}`:

```typescript
cn(
  isInvalid && '[&>input]:border-destructive [&>textarea]:border-destructive [&>select]:border-destructive',
  valid === true && '[&>input]:border-success [&>textarea]:border-success',
)
```

The descendant selector targets `<input>`, `<textarea>`, and `<select>` one
level deep. It does NOT target deeper-nested inputs (e.g., inside a `<div>`
wrapper inside `{children}`).

---

## 4. Sub-component Tailwind recipes

### Label

```
text-sm font-medium leading-none
```

Optional marker:
```
ml-1 text-xs text-muted-foreground font-normal
```

### HintLabel

```
text-xs text-muted-foreground
```

### ErrorLabel

```
text-xs text-destructive
```

---

## 5. Semantic color tokens consumed

| Token class | Semantic meaning |
|---|---|
| `text-muted-foreground` | Secondary / subdued text color |
| `text-destructive` | Error / destructive action color |
| `border-destructive` | Error border color on child inputs |
| `border-success` | Success border color on child inputs |

These are design system semantic tokens (shadcn/ui CSS variable conventions),
not `--sf-input-*` tokens. Their concrete values are defined in the design
system's CSS variable layer.

---

## 6. Known gaps

- The descendant selector only targets direct-child `<input>`, `<textarea>`,
  `<select>`. Wrapped or composed children (e.g. a custom button-trigger
  select) will not receive the error border.
- No `border-success` for `<select>` elements — the selector omits it
  (`[&>input]:border-success [&>textarea]:border-success` but not select).
